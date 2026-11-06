namespace Recall.Server.Domain.Tenants.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Storage;
using Serilog;
using Users;

public static class DeleteTenant
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, FileStore fileStore)
        : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.tenant_id", request.Id);

            await permissionGuard.RequireAsync(Permission.ManageTenant(), cancellationToken);

            // The exclusive lock makes each add that takes the shared lock of the tenant wait, so no such row comes after
            // the load. A deleted user frees its identifier, so the same person can sign up again.
            var deletedCounts = await LastTenant.RunWithLocksAsync(dbContext, request.Id, async ct =>
            {
                var tenant = await dbContext.Tenants
                    .TagWithOperationCallSite("tenants.delete.load")
                    .GetById(request.Id, ct);
                await LastTenant.GuardAsync(dbContext, tenant.Id, ct);

                var rows = await TenantOwnedRows.LoadAsync(dbContext, tenant.Id, ct);
                foreach (var row in rows)
                    dbContext.Remove(row is IDeletedWithEvent deletedWithEvent ? deletedWithEvent.Delete() : row);

                dbContext.Tenants.Remove(tenant);
                await dbContext.SaveChangesAsync(ct);
                return rows.CountBy(row => row.GetType().Name).ToDictionary();
            }, cancellationToken);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.tenant.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted tenant {TenantId} and its rows {@DeletedCounts}, WAL scrubbed {WalScrubbed}", request.Id, deletedCounts, scrubbed);

            TryDeleteFilesOfTenant(request.Id);
        }

        private void TryDeleteFilesOfTenant(string tenantId)
        {
            using var activity = Telemetry.Trace([("app.tenant_id", tenantId)], "tenants.delete_files");
            try
            {
                fileStore.DeleteUnder(FileKey.ForTenant(tenantId));
                _logger.Here().Information("Deleted the files of deleted tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                Telemetry.RecordError(activity, ex);
                _logger.Here().Error(ex, "Could not delete the files of deleted tenant {TenantId}", tenantId);
            }
        }
    }
}
