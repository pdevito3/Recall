namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using UserSettings;

public static class ApplyWaitingCleanup
{
    public sealed record Command(string Id, WaitingCleanupApplyDto Dto) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, SessionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.apply_waiting_cleanup.load")
                .GetById(request.Id, cancellationToken);
            var text = await dbContext.CleanedRevisions
                .TagWithOperationCallSite("sessions.apply_waiting_cleanup.revision")
                .Where(revision => revision.SessionId == session.Id && revision.Number == request.Dto.Revision)
                .Select(revision => revision.Text)
                .FirstOrDefaultAsync(cancellationToken);

            session.ApplyWaitingCleanup(request.Dto.Revision, text ?? string.Empty, request.Dto.BaseCleanedRevision, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Took the waiting Cleanup result into Cleaned of session {SessionId}", session.Id);

            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }
    }
}
