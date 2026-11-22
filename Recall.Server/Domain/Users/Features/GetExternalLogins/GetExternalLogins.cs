namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Auth;
using Auth.Entities;
using Databases;
using Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetExternalLogins
{
    /// <param name="UserId">The user whose links to list, or null for the caller.</param>
    public sealed record Query(string? UserId) : IRequest<IReadOnlyList<ExternalLoginDto>>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes)
        : IRequestHandler<Query, IReadOnlyList<ExternalLoginDto>>
    {
        public async Task<IReadOnlyList<ExternalLoginDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            // Only a person in a web or native session, so a token or an agent cannot learn the ways in of a person (IDP-19).
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            var userId = request.UserId ?? caller.UserId;
            Activity.Current?.SetTag("app.user_id", userId);
            UserAction.ManageExternalLogins().GuardBeforeLoad(caller, userId);

            var user = await dbContext.Users
                .TagWithOperationCallSite("users.external_logins.user")
                .AsNoTracking()
                .Include(u => u.UserPermissions)
                .GetById(userId, cancellationToken);
            user.GuardAllows(UserAction.ManageExternalLogins(), caller);

            var links = await dbContext.Set<ExternalLogin>()
                .AsNoTracking()
                .TagWithOperationCallSite("users.external_logins.list")
                .Where(l => l.UserId == user.Id)
                .OrderBy(l => l.CreatedAt).ThenBy(l => l.Issuer).ThenBy(l => l.Subject)
                .ToListAsync(cancellationToken);
            var providers = modes.Oidc;
            return links.Select(link =>
            {
                var provider = providers.FirstOrDefault(mode => mode.HasIssuer(link.Issuer));
                return new ExternalLoginDto
                {
                    Issuer = link.Issuer,
                    Subject = link.Subject,
                    DisplayLabel = link.DisplayLabel,
                    ProviderId = provider?.Id,
                    ProviderName = provider?.DisplayName,
                    CreatedAt = link.CreatedAt,
                    LastLoginAt = link.LastLoginAt,
                };
            }).ToList();
        }
    }
}
