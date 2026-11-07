namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class SaveRawDraft
{
    public sealed record Command(string Id, DraftSaveDto Dto) : IRequest<DraftSaveResultDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, DraftSaveResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<DraftSaveResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.save_raw.load")
                .GetById(request.Id, cancellationToken);

            var save = DraftSave.Of(request.Dto.Text, request.Dto.BaseRevision, request.Dto.SavedAt);
            var earlier = await dbContext.RawRevisions
                .TagWithOperationCallSite("sessions.save_raw.earlier")
                .Where(r => r.SessionId == session.Id && r.SavedAt == save.SavedAt && r.Text == save.Text)
                .Select(r => new { r.Number, r.WasDraft })
                .FirstOrDefaultAsync(cancellationToken);

            // The Revision committed after the load of the Session, so the load holds an older latest number. Run again on a new load.
            if (earlier?.Number > session.Raw.LatestRevision)
                throw new StaleVersionException();

            var decision = session.SaveRaw(save, earlier is null ? null : new DraftAnswer(earlier.WasDraft ? earlier.Number : null));
            await dbContext.SaveChangesAsync(cancellationToken);

            Activity.Current?.SetTag("app.session.save_outcome", decision.Outcome.ToString());
            _logger.Here().Information("Saved Raw of session {SessionId}, outcome {Outcome}", session.Id, decision.Outcome);
            return session.ToSaveResult(session.Raw, decision);
        }
    }
}
