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

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, DraftSaveResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<DraftSaveResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.save_cleaned.load")
                .GetById(request.Id, cancellationToken);

            var save = DraftSave.Of(request.Dto.Text, request.Dto.BaseRevision, request.Dto.SavedAt);
            var earlier = await dbContext.CleanedRevisions
                .TagWithOperationCallSite("sessions.save_cleaned.earlier")
                .Where(r => r.SessionId == session.Id && r.SavedAt == save.SavedAt && r.Text == save.Text)
                .Select(r => (DraftAnswer?)new DraftAnswer(r.WasDraft ? r.Number : null))
                .FirstOrDefaultAsync(cancellationToken);

            var decision = session.SaveCleaned(save, earlier);
            await dbContext.SaveChangesAsync(cancellationToken);

            Activity.Current?.SetTag("app.session.save_outcome", decision.Outcome.ToString());
            _logger.Here().Information("Saved Cleaned of session {SessionId}, outcome {Outcome}", session.Id, decision.Outcome);
            return session.ToSaveResult(session.Cleaned, decision);
        }
    }
}
