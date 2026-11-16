namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class GetUserPersonalAccessTokens
{
    public sealed record Query(string UserId) : IRequest<IReadOnlyList<PersonalAccessTokenDto>>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, SessionStore store, TimeProvider timeProvider)
        : IRequestHandler<Query, IReadOnlyList<PersonalAccessTokenDto>>
    {
        public async Task<IReadOnlyList<PersonalAccessTokenDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.UserId);

            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.RequireSelfOr(request.UserId, Permission.ReadUsers());

            var user = await dbContext.Users
                .TagWithOperationCallSite("personal_access_tokens.list_of_user.user")
                .AsNoTracking()
                .GetById(request.UserId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            return (await store.ListPersonalAccessTokensAsync(user, cancellationToken))
                .Select(row => row.ToPersonalAccessTokenDto(caller, now))
                .ToList();
        }
    }
}
