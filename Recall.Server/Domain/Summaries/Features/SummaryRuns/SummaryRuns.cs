namespace Recall.Server.Domain.Summaries.Features;

using System.Diagnostics;
using Databases;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

/// <summary>Begins the run of a job on a period. A run that a request began and that is still current goes on with the job.</summary>
public static class BeginSummaryRun
{
    public sealed record Command(PeriodKey Key, string? RequestedRunId, Guid JobId) : IRequest<Result>, IRunsAgainWhenRaced;

    public sealed record Result(string SummaryId, string RunId);

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, Result>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.begin_run.load")
                .FindAsync(request.Key, cancellationToken);

            string runId;
            if (summary is null)
            {
                summary = Summary.WithFirstRun(owner.TenantId, owner.UserId, request.Key, request.JobId);
                runId = summary.RunId!;
                await dbContext.Summaries.AddAsync(summary, cancellationToken);
            }
            else if (request.RequestedRunId is { } requested && summary.TakeRun(requested, request.JobId))
            {
                runId = requested;
            }
            else
            {
                runId = summary.BeginRun(request.JobId);
            }

            Activity.Current?.SetTag("app.summary_id", summary.Id);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Began summary run {SummaryRunId} of summary {SummaryId} in job {JobId}", runId, summary.Id, request.JobId);
            return new Result(summary.Id, runId);
        }
    }
}

/// <summary>Stores the words of a run. A run that is no longer current stores nothing.</summary>
public static class CompleteSummaryRun
{
    public sealed record Command(string SummaryId, string RunId, string Content, SourceState Sources) : IRequest<bool>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, bool>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.summary_id", request.SummaryId);
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.complete_run.load")
                .FindByIdAsync(request.SummaryId, cancellationToken);
            if (summary is null || !summary.Complete(request.RunId, request.Content, request.Sources, timeProvider.GetUtcNow()))
                return false;

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Summary run {SummaryRunId} of summary {SummaryId} is ready from {SummarySourceCount} sources", request.RunId, summary.Id, request.Sources.Count);
            return true;
        }
    }
}

/// <summary>Ends a run with no words. A run that is no longer current changes nothing.</summary>
public static class FailSummaryRun
{
    public sealed record Command(string SummaryId, string RunId, SummaryFailure Failure) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.summary_id", request.SummaryId);
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.fail_run.load")
                .FindByIdAsync(request.SummaryId, cancellationToken);
            if (summary is null || !summary.Fail(request.RunId, request.Failure))
                return;

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Summary run {SummaryRunId} of summary {SummaryId} failed, reason {SummaryFailure}", request.RunId, summary.Id, request.Failure);
        }
    }
}

/// <summary>
/// Ends each run that a job still holds, as <see cref="SummaryFailure.Interrupted"/>. A job calls it at its end, and the watch
/// calls it for a job that ended with no result, so no Summary stays <c>Generating</c>.
/// </summary>
public static class EndSummaryRunsOfJob
{
    public sealed record Command(Guid JobId) : IRequest<int>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, int>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<int> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.job_id", request.JobId);
            var held = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.end_runs_of_job.load")
                .Where(s => s.RunJobId == request.JobId && s.RunState == SummaryRunState.Generating)
                .ToListAsync(cancellationToken);
            foreach (var summary in held)
                summary.Fail(summary.RunId!, SummaryFailure.Interrupted);
            if (held.Count == 0)
                return 0;

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Ended {SummaryCount} summary runs of job {JobId} as interrupted", held.Count, request.JobId);
            return held.Count;
        }
    }
}

/// <summary>Removes the Summary of a period that has no sources now, because such a period has no Summary.</summary>
public static class RemoveSummaryWithNoSources
{
    public sealed record Command(PeriodKey Key) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.remove.load")
                .FindAsync(request.Key, cancellationToken);
            if (summary is null)
                return;

            Activity.Current?.SetTag("app.summary_id", summary.Id);
            dbContext.Summaries.Remove(summary);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Removed summary {SummaryId}, because its period has no sources", summary.Id);
        }
    }
}
