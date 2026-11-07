namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Exceptions;
using MediatR;
using Resources;
using Serilog;

/// <summary>The end of a run that gave no result. A run that ended, a newer run, or a deleted Session makes this change nothing.</summary>
public static class FailCleanupRun
{
    public sealed record Command(string SessionId, string RunId, CleanupFailure Failure) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.SessionId);

            Session session;
            try
            {
                session = await dbContext.Sessions
                    .TagWithOperationCallSite("sessions.fail_cleanup.load")
                    .GetById(request.SessionId, cancellationToken);
            }
            catch (NotFoundException)
            {
                return;
            }

            if (session.CleanupRun != CleanupRunState.Running || session.CleanupRunId != request.RunId)
                return;

            session.FailCleanup(request.Failure);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Cleanup {CleanupRunId} of session {SessionId} failed, reason {CleanupFailure}", request.RunId, session.Id, request.Failure);
        }
    }
}
