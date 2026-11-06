namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Recordings;
using Recordings.Features;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Storage;
using Serilog;

public static class DeleteUser
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, FileStore fileStore) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.Id);

            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            UserAction.Delete().GuardBeforeLoad(caller, request.Id);

            var user = await LastAdmin.RunWithTenantLockAsync(dbContext, request.Id, async ct =>
            {
                var user = await dbContext.Users
                    .TagWithOperationCallSite("users.delete.load")
                    .Include(u => u.UserPermissions)
                    .GetById(request.Id, ct);
                user.GuardAllows(UserAction.Delete(), caller);
                await LastAdmin.GuardAsync(dbContext, user, ct);

                // Remove permissions first (due to PropertyAccessMode.Field)
                foreach (var permission in user.UserPermissions.ToList())
                {
                    dbContext.UserPermissions.Remove(permission);
                }

                dbContext.Users.Remove(user.Delete());
                await dbContext.SaveChangesAsync(ct);
                return user;
            }, cancellationToken);

            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.OwnerFolder(user.TenantId, user.Id), user.Id, _logger);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.user.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted user {UserId}, WAL scrubbed {WalScrubbed}", user.Id, scrubbed);
        }
    }
}
