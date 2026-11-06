namespace Recall.Server.Domain.Sessions.Jobs;

using System.Text.Json;
using BackWave.Storage;
using Features;
using MediatR;
using Resources;
using Serilog;
using Tenants;
using Users;

/// <summary>
/// Ends the run of a job that no code of the job can end: a job in the dead letters at the attempt ceiling, a job that the Owner
/// cancels, and a quarantined job. A BackWave observer would do this, but its claim writes on each poll, so an idle server
/// would write to the disk. This watch only reads while no such job appears.
/// </summary>
public sealed class CleanupJobWatch(IJobStore store, IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private static readonly JobState[] EndedWithNoResult = [JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined];
    private static readonly JsonSerializerOptions AnyCase = new(JsonSerializerDefaults.Web);

    /// <summary>The largest page that the store gives. A shorter page is the last one.</summary>
    private const int PageSize = 200;

    private readonly ILogger _logger = Log.ForContext<CleanupJobWatch>();
    private readonly HashSet<Guid> _ended = [];

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
                _logger.Here().Warning("The watch of the Cleanup jobs failed ({FaultType}), and it tries again.", fault.GetType().FullName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task EndRunsOfEndedJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var state in EndedWithNoResult)
        {
            long? after = null;
            while (true)
            {
                var jobs = await store.ListJobsAsync(new JobQuery { State = state, WireName = CleanupJob.WireName, AfterSequence = after, MaxResults = PageSize }, cancellationToken);
                foreach (var job in jobs.Where(job => !_ended.Contains(job.JobId)))
                {
                    try
                    {
                        await EndRunAsync(job, cancellationToken);
                        _ended.Add(job.JobId);
                    }
                    catch (Exception fault) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Here().Warning("The watch could not end the run of Cleanup job {JobId} ({FaultType}), and it tries again.", job.JobId, fault.GetType().FullName);
                    }
                }

                if (jobs.Count < PageSize)
                    break;
                after = jobs[^1].Sequence;
            }
        }
    }

    private async Task EndRunAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (PayloadOf(job) is not { } payload)
        {
            _logger.Here().Warning("Cleanup job {JobId} has a payload that does not read, so no run can end from it.", job.JobId);
            return;
        }

        await using var scope = scopeFactory.CreateJournalScope(payload.TenantId, payload.UserId);
        await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new FailCleanupRun.Command(payload.SessionId, payload.RunId, CleanupFailure.Interrupted), cancellationToken);
    }

    /// <summary>Null when the payload holds no JSON of this job, or lacks a valid ID. An old job or a hand edit can do that.</summary>
    private static CleanupSession? PayloadOf(JobRecord job)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CleanupSession>(job.Payload.Span, AnyCase);
            return payload is { SessionId: not null, RunId: not null } && EntityId.IsValid(payload.TenantId, Tenant.IdPrefix) && EntityId.IsValid(payload.UserId, User.IdPrefix) ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
