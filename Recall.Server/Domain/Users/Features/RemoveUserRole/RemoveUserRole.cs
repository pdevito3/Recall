namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class RemoveUserRole
{
    public sealed record Command(string Id, string Role) : IRequest<UserDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes) : IRequestHandler<Command, UserDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.Id);

            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            UserAction.RemoveRole().GuardBeforeLoad(caller, request.Id);
            var role = UserRole.Of(request.Role);
            Activity.Current?.SetTag("app.user.role", role.Value);

            var (user, removed) = await LastAdmin.RunWithTenantLockAsync(dbContext, request.Id, async ct =>
            {
                var user = await dbContext.Users
                    .TagWithOperationCallSite("users.remove_role.load")
                    .Include(u => u.UserPermissions)
                    .Include(u => u.Roles)
                    .GetById(request.Id, ct);
                user.GuardAllows(UserAction.RemoveRole(), caller);
                // Only the role Admin makes a user an Admin of the LastAdmin rule.
                if (role == UserRole.Admin() && user.HasRole(role))
                    await LastAdmin.GuardAsync(dbContext, modes, user, ct);

                var change = user.RemoveRole(role);
                dbContext.Track(change);
                await dbContext.SaveChangesAsync(ct);
                return (user, change.Changed);
            }, cancellationToken);

            if (removed)
            {
                _logger.Here().Information("Removed the role {Role} from user {UserId}", role.Value, user.Id);
                UserMetrics.RoleChanged.Add(1,
                    new KeyValuePair<string, object?>("app.user.role", role.Value),
                    new KeyValuePair<string, object?>("app.user.role_change", "removed"));
            }

            return user.ToUserDto(caller, await UserLoginNames.OfAsync(dbContext, user.Id, cancellationToken));
        }
    }
}
