namespace Recall.Server.Auth;

using Databases;
using Domain.AuditEntries;
using Domain.Users;
using Entities;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public enum LinkOutcome
{
    Linked,
    AlreadyLinked,
    LinkedToAnotherUser,
    NoUser,
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
