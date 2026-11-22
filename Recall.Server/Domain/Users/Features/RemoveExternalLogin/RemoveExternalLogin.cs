namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using AuditEntries;
using Auth;
using Auth.Entities;
using Databases;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class RemoveExternalLogin
{
    /// <param name="UserId">The user whose link to remove, or null for the caller.</param>
    /// <param name="Issuer">The issuer of the link.</param>
    /// <param name="Subject">The subject of the identity at that issuer.</param>
    public sealed record Command(string? UserId, string Issuer, string Subject) : IRequest
    {
        public override string ToString() => $"[remove external login of {UserId ?? "the caller"}]";
    }

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            var userId = request.UserId ?? caller.UserId;
            Activity.Current?.SetTag("app.user_id", userId);
            UserAction.ManageExternalLogins().GuardBeforeLoad(caller, userId);

            // A removal can take the last way in of an Admin, so it holds the exclusive lock of the tenant (IDP-12, IDP-14).
            var removed = await LastAdmin.RunWithTenantLockAsync(dbContext, userId, async ct =>
            {
                var user = await dbContext.Users
                    .TagWithOperationCallSite("users.remove_external_login.user")
                    .Include(u => u.UserPermissions)
                    .GetById(userId, ct);
                user.GuardAllows(UserAction.ManageExternalLogins(), caller);

                // A link that is gone is the end state that the caller asked for, so a second removal also succeeds.
                if (!StorableText.CanStore(request.Issuer) || !StorableText.CanStore(request.Subject))
                    return null;
                var link = await dbContext.Set<ExternalLogin>()
                    .TagWithOperationCallSite("users.remove_external_login.link")
                    .FirstOrDefaultAsync(l => l.UserId == user.Id && l.Issuer == request.Issuer && l.Subject == request.Subject, ct);
                if (link is null)
                    return null;

                await LastAdmin.GuardLinkRemovalAsync(dbContext, modes, user, link, ct);
                dbContext.Set<ExternalLogin>().Remove(link);
                dbContext.RecordAction(new ExternalLoginRemoved(AuditSubject.Of(user), link.Issuer, link.Subject, link.DisplayLabel));
                await dbContext.SaveChangesAsync(ct);
                return link.Issuer;
            }, cancellationToken);

            if (removed is not null)
                _logger.Here().Information("Removed a login of issuer {Issuer} from user {UserId}", removed, userId);
        }
    }
}
