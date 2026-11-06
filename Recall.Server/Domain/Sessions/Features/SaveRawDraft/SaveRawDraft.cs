namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class SaveRawDraft
{
    public sealed record Command(string Id, DraftSaveDto Dto) : IRequest<DraftSaveResultDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, DraftSaveResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<DraftSaveResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.save_raw.load")
                .GetById(request.Id, cancellationToken);

            var save = DraftSave.Of(request.Dto.Text, request.Dto.BaseRevision, request.Dto.SavedAt, timeProvider.GetUtcNow());
            var isReplay = session.Raw.NeedsReplayLookup(save) && await dbContext.RawRevisions
                .TagWithOperationCallSite("sessions.save_raw.replay")
                .AnyAsync(r => r.SessionId == session.Id && r.ClaimedSavedAt == save.ClaimedSavedAt && r.Text == save.Text, cancellationToken);

            var outcome = session.SaveRaw(save, isReplay);
            await dbContext.SaveChangesAsync(cancellationToken);

            Activity.Current?.SetTag("app.session.save_outcome", outcome.ToString());
            _logger.Here().Information("Saved Raw of session {SessionId}, outcome {Outcome}", session.Id, outcome);
            return session.ToSaveResult(session.Raw, outcome);
        }
    }
}
