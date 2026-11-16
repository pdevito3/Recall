namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class GetPersonalAccessTokens
{
    public sealed record Query : IRequest<IReadOnlyList<PersonalAccessTokenDto>>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, SessionStore store, TimeProvider timeProvider)
        : IRequestHandler<Query, IReadOnlyList<PersonalAccessTokenDto>>
    {
        public async Task<IReadOnlyList<PersonalAccessTokenDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            var member = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            Activity.Current?.SetTag("app.user_id", member.UserId);

            var user = await dbContext.Users
                .TagWithOperationCallSite("personal_access_tokens.list.user")
                .AsNoTracking()
                .GetById(member.UserId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            return (await store.ListPersonalAccessTokensAsync(user, cancellationToken))
                .Select(row => row.ToPersonalAccessTokenDto(member, now))
                .ToList();
        }
    }
}
