namespace Recall.Server.Domain.Tenants;

using BackWave.Jobs;
using Databases;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Jobs;
using Resources.Storage;
using Serilog;

public sealed class DeleteTenantFilesJob(IServiceScopeFactory scopeFactory, FileStore fileStore)
{
    private readonly ILogger _logger = Log.ForContext<DeleteTenantFilesJob>();

    [Job("delete-tenant-files", Queue = JobHosting.SystemQueue)]
    public async Task DeleteTenantFilesAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var isDeleted = await dbContext.Tenants
            .TagWithOperationCallSite("tenants.delete_files.is_deleted")
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.IsDeleted, cancellationToken);
        if (!isDeleted)
            return;

        fileStore.DeleteUnder(FileKey.ForTenant(tenantId));
        _logger.Here().Information("Deleted the files of deleted tenant {TenantId} on a retry", tenantId);
    }
}
