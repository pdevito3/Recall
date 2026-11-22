namespace Recall.Server.Auth;

using Databases;
using Domain.AuditEntries;
using Domain.Users;
using Entities;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public enum LinkOutcome
{
    Linked,
    AlreadyLinked,
    LinkedToAnotherUser,
    NoUser,

    /// <summary>The temporary password of a link after it changed, expired, or went, so the link adds nothing (IDP-20).</summary>
    CredentialGone,
}

/// <summary>
/// Adds the link of an identity to the user of the session that started it (IDP-11). It never looks at an email: the only user that a
/// link can name is the one of the session, and an identity that is linked to anyone else stays as it is.
/// </summary>
/// <remarks>
/// Each run takes a scope of the caller, not a system scope. The callback makes the person of the session its caller, so the save names
/// that person as the actor of <see cref="ExternalLoginLinked"/>, and a query of users sees only the tenant of that person.
/// </remarks>
public sealed class LinkExternalLogin(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    private const int MaxRuns = 3;

    private readonly ILogger _logger = Log.ForContext<LinkExternalLogin>();

    public async Task<LinkOutcome> LinkAsync(string userId, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        for (var run = 1; ; run++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                return await LinkOnceAsync(dbContext, userId, identity, cancellationToken);
            }
            // Two links of one identity at once: the loser reads the row of the winner on its next run.
            catch (ConstraintConflictException conflict) when (conflict.Constraint == "pk_auth_external_logins" && run < MaxRuns)
            {
            }
        }
    }

    /// <summary>
    /// The link after a right temporary password while password login is Off (IDP-20). The temporary password must still be the one issued
    /// at <paramref name="changedAt"/>. One save adds the link under IDP-11 and deletes the temporary credential, and the version of the
    /// credential makes the save stale when it changed in between. An identity that is linked to another user changes nothing, so the
    /// temporary password still works. The caller of the request must be the user, so the save names them as its actor.
    /// </summary>
    public async Task<(LinkOutcome Outcome, ProvisionOutcome.Provisioned? User)> LinkAfterTemporaryPasswordAsync(
        string userId, DateTimeOffset? changedAt, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        for (var run = 1; ; run++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                return await LinkAfterTemporaryPasswordOnceAsync(dbContext, userId, changedAt, identity, cancellationToken);
            }
            catch (ConstraintConflictException conflict) when (conflict.Constraint == "pk_auth_external_logins" && run < MaxRuns)
            {
            }
            // An issue, a sign-in, or a claim changed the credential after the load, so this temporary password is not the one of the attempt.
            catch (StaleVersionException)
            {
                return (LinkOutcome.CredentialGone, null);
            }
        }
    }

    private async Task<(LinkOutcome Outcome, ProvisionOutcome.Provisioned? User)> LinkAfterTemporaryPasswordOnceAsync(
        AppDbContext dbContext, string userId, DateTimeOffset? changedAt, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var tenantId = await dbContext.Users
            .AsNoTracking()
            .TagWithOperationCallSite("auth.link_temporary.tenant")
            .Where(u => u.Id == userId)
            .Select(u => u.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null)
            return (LinkOutcome.NoUser, null);

        return await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
        {
            var user = await dbContext.Users
                .TagWithOperationCallSite("auth.link_temporary.user")
                .FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return (LinkOutcome.NoUser, (ProvisionOutcome.Provisioned?)null);

            // In the lock, so a removal of this link, which holds the exclusive lock, cannot land between this read and the delete of the
            // temporary password and leave the user with no way in.
            var owner = await dbContext.Set<ExternalLogin>()
                .AsNoTracking()
                .TagWithOperationCallSite("auth.link_temporary.find")
                .Where(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject)
                .Select(l => l.UserId)
                .FirstOrDefaultAsync(ct);
            if (owner is not null && owner != userId)
                return (LinkOutcome.LinkedToAnotherUser, null);

            var now = timeProvider.GetUtcNow();
            var credential = await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("auth.link_temporary.credential")
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (credential is not { State: PasswordState.Temporary } || credential.ChangedAt != changedAt || credential.IsExpired(now))
                return (LinkOutcome.CredentialGone, null);

            dbContext.Set<PasswordCredential>().Remove(credential);
            dbContext.RecordAction(new TemporaryPasswordEnded(AuditSubject.Of(user)));
            if (owner is null)
            {
                var link = new ExternalLogin
                {
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    UserId = user.Id,
                    CreatedAt = now,
                    LastLoginAt = now,
                    DisplayLabel = NewPerson.LabelOf(identity.Profile),
                };
                dbContext.Set<ExternalLogin>().Add(link);
                dbContext.RecordAction(new ExternalLoginLinked(AuditSubject.Of(user), link.Issuer, link.Subject, link.DisplayLabel));
            }

            await dbContext.SaveChangesAsync(ct);
            _logger.Here().Information("Linked a login of issuer {Issuer} to user {UserId} after a temporary password, and deleted the temporary password", identity.Issuer, user.Id);
            return (owner is null ? LinkOutcome.Linked : LinkOutcome.AlreadyLinked, new ProvisionOutcome.Provisioned(user.Id, user.FullName));
        }, cancellationToken);
    }

    private async Task<LinkOutcome> LinkOnceAsync(AppDbContext dbContext, string userId, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var owner = await dbContext.Set<ExternalLogin>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.link.find")
            .Where(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject)
            .Select(l => l.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is not null)
            return owner == userId ? LinkOutcome.AlreadyLinked : LinkOutcome.LinkedToAnotherUser;

        var tenantId = await dbContext.Users
            .AsNoTracking()
            .TagWithOperationCallSite("auth.link.tenant")
            .Where(u => u.Id == userId)
            .Select(u => u.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null)
            return LinkOutcome.NoUser;

        // The shared lock keeps a delete of the user, which holds the exclusive one, from saving between the load and the save.
        return await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
        {
            var user = await dbContext.Users
                .TagWithOperationCallSite("auth.link.user")
                .FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return LinkOutcome.NoUser;

            var now = timeProvider.GetUtcNow();
            var link = new ExternalLogin
            {
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                UserId = user.Id,
                CreatedAt = now,
                LastLoginAt = now,
                DisplayLabel = NewPerson.LabelOf(identity.Profile),
            };
            dbContext.Set<ExternalLogin>().Add(link);
            dbContext.RecordAction(new ExternalLoginLinked(AuditSubject.Of(user), link.Issuer, link.Subject, link.DisplayLabel));
            await dbContext.SaveChangesAsync(ct);

            _logger.Here().Information("Linked a login of issuer {Issuer} to user {UserId}", link.Issuer, user.Id);
            return LinkOutcome.Linked;
        }, cancellationToken);
    }
}

/// <summary>A link after a temporary password deleted that temporary password (IDP-20). It holds no password and no hash.</summary>
[AuditActionName("temporary_password_ended")]
public sealed record TemporaryPasswordEnded(AuditSubject Subject) : AuditAction(Subject);
