namespace Recall.Server.Domain.Users;

using Auth;
using Auth.Entities;
using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Resources;

/// <summary>
/// A tenant keeps an Admin who holds each Admin permission, because a caller can give only what the caller holds, so only such an
/// Admin can make another Admin or give a permission back. An Admin who can sign in, with a link to a Usable provider or a password that is not Stopped, keeps
/// another such Admin in its place, because a user with neither cannot sign in. With no such Admin, only a claim file recovers the install. The rule is over a
/// set of rows, so each write that can break it holds the exclusive lock of the tenant. See <c>docs/backend/concurrency.md</c>,
/// section "Lock".
/// </summary>
public static class LastAdmin
{
    public const string RefusedMessage =
        "Recall refused this change, because the install needs an Admin who can sign in and manage access. Give another person who has signed in the role Admin and each Admin permission, then try again.";

    public static async Task<T> RunWithTenantLockAsync<T>(
        AppDbContext dbContext, string userId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .TagWithOperationCallSite("users.last_admin.tenant")
            .GetById(userId, cancellationToken);
        return await RootLock.Tenant.RunExclusiveAsync(dbContext, user.TenantId, work, cancellationToken);
    }

    /// <exception cref="ConflictException">The change would leave the tenant with no Admin who holds each Admin permission, or would remove the last such Admin who can sign in.</exception>
    public static async Task GuardAsync(AppDbContext dbContext, LoginModes modes, User user, CancellationToken cancellationToken)
    {
        var adminRole = UserRole.Admin().Value;
        if (user.Role.Value != adminRole)
            return;

        var adminPermissions = UserRole.Admin().GetDefaultPermissions().Length;
        // A link counts only while a Usable provider has its issuer (IDP-14). The guard reads the list in the lock, so a delete of a
        // provider that ended before it is in the list.
        var issuers = modes.UsableIssuers;
        var links = dbContext.Set<ExternalLogin>().Where(link => issuers.Contains(link.Issuer));
        // A Stopped credential takes no password, so only the other states are a way to sign in (PWD-22).
        var passwords = dbContext.Set<PasswordCredential>().Where(p => p.FailedCount < PasswordCredential.StopAt);
        var another = await dbContext.Users
            .TagWithOperationCallSite("users.last_admin.another")
            .AnyAsync(
                u => u.TenantId == user.TenantId && u.Id != user.Id && u.Role.Value == adminRole
                    && u.UserPermissions.Select(held => held.Permission).Distinct().Count() == adminPermissions
                    && (!(links.Any(link => link.UserId == user.Id) || passwords.Any(p => p.UserId == user.Id))
                        || links.Any(link => link.UserId == u.Id) || passwords.Any(p => p.UserId == u.Id)),
                cancellationToken);
        if (!another)
            throw new ConflictException(string.Empty, RefusedMessage);
    }

    /// <summary>
    /// Refuses a removal of <paramref name="link"/> when it is the last way in of <paramref name="user"/> and the tenant would keep no other
    /// Admin who holds each Admin permission and can sign in (IDP-12, IDP-14). A link whose issuer no Usable provider has is no way in, so
    /// its removal takes nothing.
    /// </summary>
    /// <exception cref="ConflictException">The removal would leave the tenant with no Admin who holds each Admin permission and can sign in.</exception>
    public static async Task GuardLinkRemovalAsync(AppDbContext dbContext, LoginModes modes, User user, ExternalLogin link, CancellationToken cancellationToken)
    {
        var issuers = modes.UsableIssuers;
        if (!issuers.Contains(link.Issuer))
            return;

        var keepsAWayIn = await dbContext.Set<ExternalLogin>()
                .TagWithOperationCallSite("users.last_admin.other_link")
                .AnyAsync(l => l.UserId == user.Id && issuers.Contains(l.Issuer) && !(l.Issuer == link.Issuer && l.Subject == link.Subject), cancellationToken)
            || await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("users.last_admin.password")
                .AnyAsync(p => p.UserId == user.Id && p.FailedCount < PasswordCredential.StopAt, cancellationToken);
        if (!keepsAWayIn)
            await GuardAsync(dbContext, modes, user, cancellationToken);
    }

    /// <summary>
    /// Runs the work with the exclusive lock of each live tenant, taken in the order of the tenant ID, as a change of the ways to sign in of
    /// the whole install needs (IDP-14).
    /// </summary>
    public static async Task<T> RunWithEachTenantLockAsync<T>(AppDbContext dbContext, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var tenantIds = await dbContext.Tenants
            .AsNoTracking()
            .TagWithOperationCallSite("users.last_admin.live_tenants")
            .IgnoreQueryFilters([QueryFilterNames.Tenant])
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        return await RootLock.Tenant.RunExclusiveAsync(dbContext, tenantIds, work, cancellationToken);
    }

    /// <summary>
    /// Refuses a change of the Usable providers from the issuers <paramref name="before"/> to <paramref name="after"/> when a live tenant
    /// has an Admin who holds each Admin permission and can sign in now, and would have none after. A tenant with no such Admin now does
    /// not stop it, because the change takes no way in from that tenant.
    /// </summary>
    /// <exception cref="ConflictException">The change would leave a live tenant with no Admin who holds each Admin permission and can sign in.</exception>
    public static async Task GuardEachTenantAsync(
        AppDbContext dbContext, IReadOnlyList<string> before, IReadOnlyList<string> after, CancellationToken cancellationToken)
    {
        var adminRole = UserRole.Admin().Value;
        var adminPermissions = UserRole.Admin().GetDefaultPermissions().Length;
        var links = dbContext.Set<ExternalLogin>();
        var passwords = dbContext.Set<PasswordCredential>().Where(p => p.FailedCount < PasswordCredential.StopAt);
        var admins = dbContext.Users.Where(u => u.Role.Value == adminRole
            && u.UserPermissions.Select(held => held.Permission).Distinct().Count() == adminPermissions);
        var refused = await dbContext.Tenants
            .TagWithOperationCallSite("users.last_admin.each_tenant")
            .IgnoreQueryFilters([QueryFilterNames.Tenant])
            .AnyAsync(
                t => admins.Any(u => u.TenantId == t.Id
                        && (passwords.Any(p => p.UserId == u.Id) || links.Any(link => link.UserId == u.Id && before.Contains(link.Issuer))))
                    && !admins.Any(u => u.TenantId == t.Id
                        && (passwords.Any(p => p.UserId == u.Id) || links.Any(link => link.UserId == u.Id && after.Contains(link.Issuer)))),
                cancellationToken);
        if (refused)
            throw new ConflictException(string.Empty, RefusedMessage);
    }
}
