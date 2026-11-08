namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetCleanedRevisionPage
{
    public sealed record Query(string SessionId, CursorPageParametersDto Parameters) : IRequest<RevisionPageDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, RevisionPageDto>
    {
        public async Task<RevisionPageDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.SessionId);

            var exists = EntityId.IsValid(request.SessionId, Session.IdPrefix) && await dbContext.Sessions
                .TagWithOperationCallSite("sessions.cleaned_revisions.session")
                .AnyAsync(s => s.Id == request.SessionId, cancellationToken);
            if (!exists)
                throw new NotFoundException("The Session was not found.");

            var revisions = dbContext.CleanedRevisions
                .TagWithOperationCallSite("sessions.cleaned_revisions.page")
                .AsNoTracking()
                .Where(r => r.SessionId == request.SessionId);
            if (request.Parameters.Cursor is { } cursor)
            {
                var before = RevisionCursor.Parse(cursor);
                revisions = revisions.Where(r => r.Number < before);
            }

            var (page, hasMore) = await revisions
                .OrderByDescending(r => r.Number)
                .Select(r => new RevisionSummaryDto { Number = r.Number, SavedAt = r.SavedAt, Length = r.Text.Length, WasDraft = r.WasDraft, ChangeId = r.ChangeId, ParentChangeId = r.ParentChangeId })
                .ToKeysetPageAsync(request.Parameters.PageSize, cancellationToken);

            return new RevisionPageDto
            {
                Items = page,
                NextCursor = hasMore ? RevisionCursor.Of(page[^1].Number) : null,
            };
        }
    }
}
