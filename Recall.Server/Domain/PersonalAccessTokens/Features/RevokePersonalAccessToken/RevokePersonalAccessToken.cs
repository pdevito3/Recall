namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Users;

public static class RevokePersonalAccessToken
{
    public const string NotFoundMessage = "Personal access token was not found.";

    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, SessionStore store) : IRequestHandler<Command>
    {
        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.personal_access_token_id", request.Id);

            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);

            var ownerId = EntityId.IsValid(request.Id, PatClaims.IdPrefix)
                ? await store.OwnerOfPersonalAccessTokenAsync(request.Id, cancellationToken)
                : null;
            var user = ownerId is null
                ? null
                : await dbContext.Users
                    .TagWithOperationCallSite("personal_access_tokens.revoke.user")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == ownerId, cancellationToken);
            if (user is null)
                throw new NotFoundException(NotFoundMessage);

            Activity.Current?.SetTag("app.user_id", user.Id);
            // A caller that cannot list the tokens of another person does not learn that the token exists.
            if (!PersonalAccessTokenAction.CanRevoke(caller, user.Id))
            {
                if (!caller.Has(Permission.ReadUsers()))
                    throw new NotFoundException(NotFoundMessage);
                caller.Require(Permission.ManageUserAccess());
            }

            await store.RevokePersonalAccessTokenAsync(user, request.Id);
        }
    }
}
