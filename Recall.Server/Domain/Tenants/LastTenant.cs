namespace Recall.Server.Domain.Tenants;

using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Resources;

/// <summary>
/// An install keeps a live tenant. With none, each login gets a refusal, a restart and open signup change nothing, and a claim file
/// has no tenant to recover, so no API call can make that state. The rule reads the other tenants and two deletes hold the locks of
/// two tenants, so each delete also holds one exclusive lock of the install. See <c>docs/backend/concurrency.md</c>, section "Lock".
/// </summary>
public static class LastTenant
{
    public const string RefusedMessage =
        "Recall refused this delete, because this is the last tenant, and no one can sign in with no tenant. Keep this tenant, or ask the owner of this server to add another tenant first.";

    internal const string InstallRoot = "install";

    public static Task<T> RunWithLocksAsync<T>(
        AppDbContext dbContext, string tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        => RootLock.Tenant.RunExclusiveAsync(dbContext, tenantId, tenantToken
            => RootLock.Install.RunExclusiveAsync(dbContext, InstallRoot, work, tenantToken), cancellationToken);

    /// <exception cref="ConflictException">The delete would leave the install with no live tenant.</exception>
    public static async Task GuardAsync(AppDbContext dbContext, string tenantId, CancellationToken cancellationToken)
    {
        // The tenant filter shows a caller only the tenant of the caller, and this rule is about each other tenant.
        var another = await dbContext.Tenants
            .TagWithOperationCallSite("tenants.delete.another_live")
            .IgnoreQueryFilters([QueryFilterNames.Tenant])
            .AnyAsync(t => t.Id != tenantId, cancellationToken);
        if (!another)
            throw new ConflictException(string.Empty, RefusedMessage);
    }
}
