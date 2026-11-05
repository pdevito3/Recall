namespace Recall.Server.Domain.Users;

using Auth.Entities;
using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A tenant keeps one Admin, and an Admin with a sign-in link keeps another Admin with a link in its place. With closed signup,
/// the install is locked when no Admin can sign in with a provider. An Admin with no link does not count for that, because it
/// reaches the Server only through a provider token, if at all. The rule is over a set of rows, so each write that can break it
/// holds the exclusive lock of the tenant. See <c>docs/backend/concurrency.md</c>, section "Lock".
/// </summary>
public static class LastAdmin
{
    public const string RefusedMessage =
        "Recall kept this Admin, because the install needs an Admin who can sign in. Make another person who has signed in an Admin, then try again.";

    public static async Task<T> RunWithTenantLockAsync<T>(
        AppDbContext dbContext, string userId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .TagWithOperationCallSite("users.last_admin.tenant")
            .GetById(userId, cancellationToken);
        return await RootLock.Tenant.RunExclusiveAsync(dbContext, user.TenantId, work, cancellationToken);
    }

    /// <exception cref="ConflictException">The change would leave the tenant with no Admin, or would remove its last Admin with a link.</exception>
    public static async Task GuardAsync(AppDbContext dbContext, User user, CancellationToken cancellationToken)
    {
        var adminRole = UserRole.Admin().Value;
        if (user.Role.Value != adminRole)
            return;

        var links = dbContext.Set<ExternalLogin>();
        var another = await dbContext.Users
            .TagWithOperationCallSite("users.last_admin.another")
            .AnyAsync(
                u => u.TenantId == user.TenantId && u.Id != user.Id && u.Role.Value == adminRole
                    && (!links.Any(link => link.UserId == user.Id) || links.Any(link => link.UserId == u.Id)),
                cancellationToken);
        if (!another)
            throw new ConflictException(string.Empty, RefusedMessage);
    }
}
