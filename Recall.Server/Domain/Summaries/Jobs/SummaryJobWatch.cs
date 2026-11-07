namespace Recall.Server.Domain.Summaries.Jobs;

using System.Text.Json;
using BackWave.Storage;
using Databases;
using Features;
using MediatR;
using Resources;
using Serilog;
using Tenants;
using Users;

/// <summary>
/// Ends the runs of a Summary job that no code of the job can end: a job in the dead letters at the attempt ceiling, a job that
/// the Owner cancels, and a quarantined job. So no Summary stays <c>Generating</c>. It only reads while no such job appears.
/// </summary>
public sealed class SummaryJobWatch(IJobStore store, IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private static readonly JobState[] EndedWithNoResult = [JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined];
    private static readonly string[] WireNames = [SummaryJobs.RunWireName, SummaryJobs.RefreshWireName];
    private static readonly JsonSerializerOptions AnyCase = new(JsonSerializerDefaults.Web);

    /// <summary>The largest page that the store gives. A shorter page is the last one.</summary>
    private const int PageSize = 200;

    private readonly ILogger _logger = Log.ForContext<SummaryJobWatch>();
    // An Owner can requeue a job from the dead letters, and it can end there again with the same ID, so an end is the ID and
    // the time of the end. A pass keeps only the ends that the store still lists.
    private HashSet<(Guid JobId, DateTimeOffset? TerminalAt)> _ended = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        do
        {
            try
            {
                await EndRunsOfEndedJobsAsync(stoppingToken);
            }
            catch (Exception fault) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.Here().Warning("The watch of the Summary jobs failed ({FaultType}), and it tries again.", fault.GetType().FullName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task EndRunsOfEndedJobsAsync(CancellationToken cancellationToken)
    {
        var listed = new HashSet<(Guid, DateTimeOffset?)>();
        foreach (var wireName in WireNames)
        foreach (var state in EndedWithNoResult)
        {
            long? after = null;
            while (true)
            {
                var jobs = await store.ListJobsAsync(new JobQuery { State = state, WireName = wireName, AfterSequence = after, MaxResults = PageSize }, cancellationToken);
                foreach (var job in jobs)
                {
                    var end = (job.JobId, job.TerminalAt);
                    if (_ended.Contains(end))
                    {
                        listed.Add(end);
                        continue;
                    }

                    try
                    {
                        await EndRunsAsync(job, cancellationToken);
                        listed.Add(end);
                    }
                    catch (Exception fault) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Here().Warning("The watch could not end the runs of Summary job {JobId} ({FaultType}), and it tries again.", job.JobId, fault.GetType().FullName);
                    }
                }

                if (jobs.Count < PageSize)
                    break;
                after = jobs[^1].Sequence;
            }
        }

        _ended = listed;
    }

    private async Task EndRunsAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (PayloadOf(job) is not { } payload)
        {
            _logger.Here().Warning("Summary job {JobId} has a payload that does not read, so no run can end from it.", job.JobId);
            return;
        }

        await using var scope = scopeFactory.CreateJournalScope(new JournalOwner(payload.UserId!, payload.TenantId!));
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // A run that a request began has no job until a claim, so the payload names it.
        if (payload is { SummaryId: { } summaryId, RunId: { } runId })
            await mediator.Send(new FailSummaryRun.Command(summaryId, runId, SummaryFailure.Interrupted), cancellationToken);
        await mediator.Send(new EndSummaryRunsOfJob.Command(job.JobId), cancellationToken);
    }

    /// <summary>Null when the payload holds no JSON of these jobs, or lacks a valid owner. An old job or a hand edit can do that.</summary>
    private static SummaryJobPayload? PayloadOf(JobRecord job)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SummaryJobPayload>(job.Payload.Span, AnyCase);
            return payload is not null && EntityId.IsValid(payload.TenantId, Tenant.IdPrefix) && EntityId.IsValid(payload.UserId, User.IdPrefix) ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SummaryJobPayload(string? SummaryId, string? UserId, string? TenantId, string? RunId);
}
