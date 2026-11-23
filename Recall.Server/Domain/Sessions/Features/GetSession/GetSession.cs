namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetSession
{
    public sealed record Query(string Id) : IRequest<SessionDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, SessionDto>
    {
        public async Task<SessionDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.get")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            return session.ToSessionDto(await ProposalPeople.LoadAsync(dbContext, [session], cancellationToken));
        }
    }
}
