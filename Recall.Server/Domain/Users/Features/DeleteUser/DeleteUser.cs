namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class DeleteUser
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.Id);

            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            UserAction.Delete().GuardBeforeLoad(caller, request.Id);

            var user = await dbContext.Users
                .TagWithOperationCallSite("users.delete.load")
                .Include(u => u.UserPermissions)
                .GetById(request.Id, cancellationToken);
            user.GuardAllows(UserAction.Delete(), caller);

            // Remove permissions first (due to PropertyAccessMode.Field)
            foreach (var permission in user.UserPermissions.ToList())
            {
                dbContext.UserPermissions.Remove(permission);
            }

            dbContext.Users.Remove(user.Delete());
            await dbContext.SaveChangesAsync(cancellationToken);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!, cancellationToken);
            Activity.Current?.SetTag("app.user.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted user {UserId}, WAL scrubbed {WalScrubbed}", user.Id, scrubbed);
        }
    }
}
