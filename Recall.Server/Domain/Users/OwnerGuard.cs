namespace Recall.Server.Domain.Users;

using Databases;
using Microsoft.EntityFrameworkCore;

/// <summary>Tells if the current caller is the Owner of this install. It reads the caller of the current DI scope.</summary>
public interface IOwnerGuard
{
    /// <returns>The user ID of the caller if the caller is the Owner, or null.</returns>
    Task<string?> CallerIfOwnerAsync(CancellationToken cancellationToken);
}

public sealed class OwnerGuard(IPermissionGuard permissionGuard, AppDbContext dbContext) : IOwnerGuard
{
    public async Task<string?> CallerIfOwnerAsync(CancellationToken cancellationToken)
    {
        var caller = await permissionGuard.GetCallerAsync(cancellationToken);
        return caller is Caller.Member member && member.UserId == await OwnerIdAsync(cancellationToken)
            ? member.UserId
            : null;
    }

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
