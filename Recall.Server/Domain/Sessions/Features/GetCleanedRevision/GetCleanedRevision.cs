namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetCleanedRevision
{
    public sealed record Query(string SessionId, int Number) : IRequest<RevisionDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, RevisionDto>
    {
        public async Task<RevisionDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.SessionId);

            if (!EntityId.IsValid(request.SessionId, Session.IdPrefix))
                throw new NotFoundException("The Revision was not found.");

            var revision = await dbContext.CleanedRevisions
                .TagWithOperationCallSite("sessions.cleaned_revisions.get")
                .AsNoTracking()
                .Where(r => r.SessionId == request.SessionId && r.Number == request.Number)
                .Select(r => new RevisionDto { Number = r.Number, SavedAt = r.SavedAt, Text = r.Text, ChangeId = r.ChangeId, ParentChangeId = r.ParentChangeId })
                .FirstOrDefaultAsync(cancellationToken);
            return revision ?? throw new NotFoundException("The Revision was not found.");
        }
    }
}
