namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Jobs;
using Mappings;
using MediatR;
using Resources;
using Serilog;

/// <summary>
/// The stop that a person asks for. The state commits first, so a result that comes after it changes nothing, and then the call
/// to the provider ends. A run that already ended stays as it is, so the page shows what is true.
/// </summary>
public static class StopCleanup
{
    public sealed record Command(string Id) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, RunningCleanups running) : IRequestHandler<Command, SessionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.stop_cleanup.load")
                .GetById(request.Id, cancellationToken);

            if (session.CleanupRun == CleanupRunState.Running)
            {
                var runId = session.CleanupRunId!;
                session.FailCleanup(CleanupFailure.Stopped);
                await dbContext.SaveChangesAsync(cancellationToken);
                running.Stop(runId);
                _logger.Here().Information("Stopped Cleanup {CleanupRunId} of session {SessionId}", runId, session.Id);
            }

            return session.ToSessionDto(await ProposalPeople.LoadAsync(dbContext, [session], cancellationToken));
        }
    }
}
