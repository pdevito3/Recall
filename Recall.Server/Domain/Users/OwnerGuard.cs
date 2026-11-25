namespace Recall.Server.Domain.Users;

using Databases;
using Microsoft.EntityFrameworkCore;

/// <summary>Tells if the current caller is the Owner of this install. It reads the caller of the current DI scope.</summary>
public interface IOwnerGuard
{
    /// <returns>The user ID of the caller if the caller is the Owner, in person, or null.</returns>
    Task<string?> CallerIfOwnerAsync(CancellationToken cancellationToken);

    /// <returns>
    /// The user ID of the Owner if the caller may use the job tools for the Owner, or null. See
    /// <see cref="Caller.JobToolsUserId"/>.
    /// </returns>
    Task<string?> JobToolsCallerIfOwnerAsync(CancellationToken cancellationToken);
}

public sealed class OwnerGuard(IPermissionGuard permissionGuard, AppDbContext dbContext) : IOwnerGuard
{
    public async Task<string?> CallerIfOwnerAsync(CancellationToken cancellationToken)
        => await permissionGuard.GetCallerAsync(cancellationToken) is Caller.Member member
            ? await IfOwnerAsync(member.UserId, cancellationToken)
            : null;

    public async Task<string?> JobToolsCallerIfOwnerAsync(CancellationToken cancellationToken)
        => (await permissionGuard.GetCallerAsync(cancellationToken)).JobToolsUserId is { } userId
            ? await IfOwnerAsync(userId, cancellationToken)
            : null;

    private async Task<string?> IfOwnerAsync(string userId, CancellationToken cancellationToken)
        => userId == await OwnerIdAsync(cancellationToken) ? userId : null;

    private async Task<string?> OwnerIdAsync(CancellationToken cancellationToken)
    {
        var oldestTenantId = await dbContext.Tenants
            .TagWithOperationCallSite("users.owner.oldest_tenant")
            .IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldestTenantId is null)
            return null;

        return await dbContext.Users
            .TagWithOperationCallSite("users.owner.first_admin")
            .OwnerFirstIn(oldestTenantId)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
