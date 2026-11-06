namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class SaveCleanedDraft
{
    public sealed record Command(string Id, DraftSaveDto Dto) : IRequest<DraftSaveResultDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, DraftSaveResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<DraftSaveResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.save_cleaned.load")
                .GetById(request.Id, cancellationToken);

            var save = DraftSave.Of(request.Dto.Text, request.Dto.BaseRevision, request.Dto.SavedAt, timeProvider.GetUtcNow());
            var isReplay = session.Cleaned.NeedsReplayLookup(save) && await dbContext.CleanedRevisions
                .TagWithOperationCallSite("sessions.save_cleaned.replay")
                .AnyAsync(r => r.SessionId == session.Id && r.ClaimedSavedAt == save.ClaimedSavedAt && r.Text == save.Text, cancellationToken);

            var outcome = session.SaveCleaned(save, isReplay);
            await dbContext.SaveChangesAsync(cancellationToken);

            Activity.Current?.SetTag("app.session.save_outcome", outcome.ToString());
            _logger.Here().Information("Saved Cleaned of session {SessionId}, outcome {Outcome}", session.Id, outcome);
            return session.ToSaveResult(session.Cleaned, outcome);
        }
    }
}
