namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class AddUser
{
    public sealed record Command(UserForCreationDto Dto) : IRequest<UserDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard) : IRequestHandler<Command, UserDto>
    {
        // The key in the errors of the problem is the name of the request field
        private const string TenantIdField = "tenantId";

        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserDto> Handle(Command request, CancellationToken cancellationToken)
        {
            // The requested tenant first, so that a refused request also has it on the span
            Activity.Current?.SetTag("app.tenant_id", request.Dto.TenantId);

            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            caller.Require(Permission.ManageUsers());
            var roles = request.Dto.Roles.Select((name, i) => UserRole.Of(name, $"roles[{i}]")).Distinct().ToList();
            Activity.Current?.SetTag("app.user.roles", string.Join(",", roles.Select(r => r.Value)));
            // A caller can give only what the caller holds (ADR 0009).
            caller.Require(EffectivePermissions.FromRoles(roles));

            var tenantId = ResolveTenantId(request.Dto.TenantId);
            Activity.Current?.SetTag("app.tenant_id", tenantId);

            // The shared lock keeps a tenant delete out until the save, so the user does not land in a deleted tenant.
            var added = await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
            {
                // The tenant filter and the soft delete filter apply, so a deleted tenant does not exist here.
                var tenantExists = await dbContext.Tenants
                    .TagWithOperationCallSite("users.add.tenant_exists")
                    .AnyAsync(t => t.Id == tenantId, ct);
                if (!tenantExists)
                    throw new ValidationException(TenantIdField, "The tenant does not exist.");

                // A session puts the user ID in sub, and the Server finds the caller by identifier, so the identifier is the ID.
                var id = EntityId.New(User.IdPrefix);
                var user = User.Create(request.Dto.ToUserForCreation(tenantId) with { Id = id, Identifier = id });
                Activity.Current?.SetTag("app.user_id", user.Id);

                await dbContext.Users.AddAsync(user, ct);
                await dbContext.SaveChangesAsync(ct);
                return user;
            }, cancellationToken);

            _logger.Here().Information("Added user {UserId} to tenant {TenantId} with roles {Roles}", added.Id, tenantId, roles.Select(r => r.Value).ToList());
            
            // TODO handle IDP creation

            return added.ToUserDto(caller, null);
        }

        // A user adds to their own tenant. A caller that sees all tenants has no tenant of its own,
        // so it must name the tenant.
        private string ResolveTenantId(string? requestedTenantId)
        {
            var scope = dbContext.CurrentTenantScope;
            if (string.IsNullOrWhiteSpace(requestedTenantId))
            {
                if (scope.TenantId is { } ownTenantId)
                    return ownTenantId;
                if (scope.IncludesAllTenants)
                    throw new ValidationException(TenantIdField, "Please provide a tenant.");
                throw new ForbiddenAccessException("You can add a user only to your own tenant.");
            }

            if (!scope.Includes(requestedTenantId))
                throw new ForbiddenAccessException("You can add a user only to your own tenant.");

            if (!EntityId.IsValid(requestedTenantId, Tenants.Tenant.IdPrefix))
                throw new ValidationException(TenantIdField, "The tenant does not exist.");

            return requestedTenantId;
        }
    }
}
