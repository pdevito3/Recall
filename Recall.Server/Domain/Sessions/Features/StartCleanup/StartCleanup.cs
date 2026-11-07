namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using System.Text.Json;
using BackWave.Storage;
using Content;
using Databases;
using Dtos;
using Exceptions;
using Jobs;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using Resources;
using Resources.Jobs;
using Serilog;
using UserSettings;

public static class StartCleanup
{
    public sealed record Command(string Id) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IJobStore store) : IRequestHandler<Command, SessionDto>
    {
        private const string CleanupField = "cleanup";
        private const int PageSize = 500;
        private static readonly JobState[] UnfinishedStates = [JobState.Scheduled, JobState.AwaitingParent, JobState.Leased];

        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.start_cleanup.load")
                .GetById(request.Id, cancellationToken);

            // A second person or a second tab joins the run, so one Session has one result at a time.
            if (session.CleanupRun != CleanupRunState.Running)
            {
                ValidationException.MustNot(string.IsNullOrWhiteSpace(ProseMirrorToPlainText.Render(session.Raw.Text)), CleanupField, "This Session has no words to clean up.");

                var hasModel = await dbContext.ModelProviders
                    .TagWithOperationCallSite("sessions.start_cleanup.provider")
                    .ActiveFirst()
                    .AnyAsync(cancellationToken);
                ValidationException.Must(hasModel, CleanupField, "An Admin must connect a model provider, pick a model, and make it active first.");

                var runId = session.BeginCleanup();
                await dbContext.SaveAndEnqueueAsync(
                    async (jobs, ct) =>
                    {
                        // In the callback, because each save of this process waits for its turn, so no other start enqueues
                        // between this read and this enqueue. The read is a plain read of the WAL, so it waits for no lock.
                        var queue = CleanupQueues.Pick(session.TenantId, await UnfinishedCleanupsAsync(ct));
                        await jobs.EnqueueOnAsync(new CleanupSession(session.Id, session.UserId, session.TenantId, runId), DateTimeOffset.UtcNow, queue, ct);
                    },
                    cancellationToken);
                _logger.Here().Information("Started Cleanup {CleanupRunId} of session {SessionId}", runId, session.Id);
            }

            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }

        private async Task<IReadOnlyCollection<(string Queue, string TenantId)>> UnfinishedCleanupsAsync(CancellationToken cancellationToken)
        {
            var unfinished = new List<(string, string)>();
            foreach (var state in UnfinishedStates)
            {
                long? after = null;
                while (true)
                {
                    var page = await store.ListJobsAsync(new JobQuery { State = state, WireName = CleanupJob.WireName, AfterSequence = after, MaxResults = PageSize }, cancellationToken);
                    unfinished.AddRange(page.Select(job => (job.Queue, JsonSerializer.Deserialize<CleanupSession>(job.Payload.Span)!.TenantId)));
                    if (page.Count < PageSize)
                        break;
                    after = page[^1].Sequence;
                }
            }

            return unfinished;
        }
    }
}
