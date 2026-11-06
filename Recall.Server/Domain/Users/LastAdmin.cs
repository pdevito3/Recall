namespace Recall.Server.Domain.Users;

using Auth.Entities;
using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A tenant keeps an Admin who holds each Admin permission, because a caller can give only what the caller holds, so only such an
/// Admin can make another Admin or give a permission back. An Admin with a sign-in link keeps another such Admin with a link in its
/// place, because a user with no link cannot sign in. With no such Admin, only a claim file recovers the install. The rule is over a
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

    /// <exception cref="ConflictException">The change would leave the tenant with no Admin who holds each Admin permission, or would remove the last such Admin with a link.</exception>
    public static async Task GuardAsync(AppDbContext dbContext, User user, CancellationToken cancellationToken)
    {
        var adminRole = UserRole.Admin().Value;
        if (user.Role.Value != adminRole)
            return;

        var adminPermissions = UserRole.Admin().GetDefaultPermissions().Length;
        var links = dbContext.Set<ExternalLogin>();
        var another = await dbContext.Users
            .TagWithOperationCallSite("users.last_admin.another")
            .AnyAsync(
                u => u.TenantId == user.TenantId && u.Id != user.Id && u.Role.Value == adminRole
                    && u.UserPermissions.Select(held => held.Permission).Distinct().Count() == adminPermissions
                    && (!links.Any(link => link.UserId == user.Id) || links.Any(link => link.UserId == u.Id)),
                cancellationToken);
        if (!another)
            throw new ConflictException(string.Empty, RefusedMessage);
    }
}
