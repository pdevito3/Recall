namespace Recall.Server.Domain.Summaries.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Jobs;
using Mappings;
using MediatR;
using Recall.Server.Resources.Jobs;
using Resources;
using Serilog;
using UserSettings;

/// <summary>
/// What a person gets when they open a period: a Summary that is missing, stale, or failed starts a run as a job, and the
/// answer comes at once with <c>Generating</c>. A Summary that is ready or that runs stays as it is. A period with no sources
/// gets no Summary, and a Summary that lost each source goes away.
/// </summary>
public static class GenerateSummary
{
    public sealed record Command(SummaryPeriod Period, DateOnly Date) : IRequest<SummaryDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider, SummarySchedules schedules) : IRequestHandler<Command, SummaryDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SummaryDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var key = PeriodKey.Holding(request.Period, request.Date);
            Activity.Current?.SetTag("app.summary.period", key.Period.ToString());

            var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
            await schedules.EnsureAsync(owner.UserId, owner.TenantId, zone, cancellationToken);
            var sources = (await dbContext.LoadSourcesAsync(zone, key, cancellationToken)).StateOf(key);
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.generate.load")
                .FindAsync(key, cancellationToken);

            if (!sources.HasSources)
            {
                if (summary is not null)
                {
                    dbContext.Summaries.Remove(summary);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return SummaryMapper.ToSummaryDto(null, key, sources);
            }

            if (summary?.StatusFor(sources) is SummaryStatus.Ready or SummaryStatus.Generating)
                return summary.ToSummaryDto(key, sources);

            string runId;
            if (summary is null)
            {
                summary = Summary.WithFirstRun(owner.TenantId, owner.UserId, key, jobId: null);
                runId = summary.RunId!;
                await dbContext.Summaries.AddAsync(summary, cancellationToken);
            }
            else
            {
                runId = summary.BeginRun(jobId: null);
            }

            Activity.Current?.SetTag("app.summary_id", summary.Id);
            await dbContext.SaveAndEnqueueAsync(
                async (jobs, ct) => await jobs.EnqueueAsync(new RunSummary(summary.Id, owner.UserId, owner.TenantId, runId), timeProvider.GetUtcNow(), ct),
                cancellationToken);
            _logger.Here().Information("Started summary run {SummaryRunId} of summary {SummaryId}, period {SummaryPeriod}", runId, summary.Id, key.Period);
            return summary.ToSummaryDto(key, sources);
        }
    }
}
