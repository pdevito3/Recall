namespace Recall.Server.Domain.Users;

using Auth.Entities;
using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A tenant keeps an Admin who can manage access, and an Admin with a sign-in link keeps another such Admin with a link in its
/// place. With closed signup, the install is locked with no such Admin, and a claim file cannot help, because a claim only adds a
/// link to an Admin. A user with no link cannot sign in. The rule is over a set of rows, so each write that can break it holds the
/// exclusive lock of the tenant. See <c>docs/backend/concurrency.md</c>, section "Lock".
/// </summary>
public static class LastAdmin
{
    public const string RefusedMessage =
        "Recall refused this change, because the install needs an Admin who can sign in and manage access. Make another person who has signed in an Admin, then try again.";

    public static async Task<T> RunWithTenantLockAsync<T>(
        AppDbContext dbContext, string userId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .TagWithOperationCallSite("users.last_admin.tenant")
            .GetById(userId, cancellationToken);
        return await RootLock.Tenant.RunExclusiveAsync(dbContext, user.TenantId, work, cancellationToken);
    }

    /// <exception cref="ConflictException">The change would leave the tenant with no Admin who manages access, or would remove the last such Admin with a link.</exception>
    public static async Task GuardAsync(AppDbContext dbContext, User user, CancellationToken cancellationToken)
    {
        var adminRole = UserRole.Admin().Value;
        if (user.Role.Value != adminRole)
            return;

        var manageAccess = Permission.ManageUserAccess();
        var links = dbContext.Set<ExternalLogin>();
        var another = await dbContext.Users
            .TagWithOperationCallSite("users.last_admin.another")
            .AnyAsync(
                u => u.TenantId == user.TenantId && u.Id != user.Id && u.Role.Value == adminRole
                    && u.UserPermissions.Any(held => held.Permission == manageAccess)
                    && (!links.Any(link => link.UserId == user.Id) || links.Any(link => link.UserId == u.Id)),
                cancellationToken);
        if (!another)
            throw new ConflictException(string.Empty, RefusedMessage);
    }
}
