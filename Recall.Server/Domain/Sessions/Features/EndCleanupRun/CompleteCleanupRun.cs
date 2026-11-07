namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Cleanup;
using Databases;
using MediatR;
using Resources;
using Serilog;

/// <summary>The end of a run of the job. A run that ended, or a newer run, makes this one change nothing.</summary>
public static class CompleteCleanupRun
{
    public sealed record Command(string SessionId, string RunId, int RawRevisionRead, CleanupOutput Output) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, CleanupCompletion completion, TimeProvider timeProvider) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.SessionId);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.complete_cleanup.load")
                .GetById(request.SessionId, cancellationToken);
            if (session.CleanupRun != CleanupRunState.Running || session.CleanupRunId != request.RunId)
                return;

            if (await completion.OutcomeOfAsync(session, request.Output, cancellationToken) is { } outcome)
                session.CompleteCleanup(outcome, request.RawRevisionRead, timeProvider.GetUtcNow());
            else
                session.FailCleanup(CleanupFailure.BadModelOutput);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Ended Cleanup {CleanupRunId} of session {SessionId}, state {CleanupRun}", request.RunId, session.Id, session.CleanupRun);
        }
    }
}
