namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class AddUserRole
{
    // The endpoint sends the role of UserRoleDto, so the rule of the DTO is also on the command
    public sealed record Command(string Id, [property: RequiredField] string Role) : IRequest<UserDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard) : IRequestHandler<Command, UserDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.Id);

            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            UserAction.AddRole().GuardBeforeLoad(caller, request.Id);
            var role = UserRole.Of(request.Role);
            Activity.Current?.SetTag("app.user.role", role.Value);
            // A caller can give only what the caller holds (ADR 0009). The add ends each Deny of a permission of the
            // role, so this check also covers each permission that the add gives back.
            caller.Require(role.Permissions());

            var user = await dbContext.Users
                .TagWithOperationCallSite("users.add_role.load")
                .Include(u => u.UserPermissions)
                .Include(u => u.Roles)
                .GetById(request.Id, cancellationToken);
            user.GuardAllows(UserAction.AddRole(), caller);

            var change = user.AddRole(role);
            dbContext.Track(change);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (change.Changed)
            {
                _logger.Here().Information("Added the role {Role} to user {UserId}, ending {ClearedOverrides} overrides",
                    role.Value, user.Id, change.ClearedOverrides.Count);
                UserMetrics.RoleChanged.Add(1,
                    new KeyValuePair<string, object?>("app.user.role", role.Value),
                    new KeyValuePair<string, object?>("app.user.role_change", "added"));
            }

            return user.ToUserDto(caller, await UserLoginNames.OfAsync(dbContext, user.Id, cancellationToken));
        }
    }
}
