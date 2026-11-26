namespace Recall.Server.Auth;

using Databases;
using Domain.Tenants;
using Domain.Tenants.Models;
using Domain.Users;
using Entities;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resources;
using Serilog;
using Serilog.Events;

public enum LoginFailure
{
    AccessDenied,
    IdpUnreachable,
    InvalidResponse,
}

public abstract record ProvisionOutcome
{
    public sealed record Provisioned(string UserId, string DisplayName) : ProvisionOutcome
    {
        public override string ToString() => $"[provisioned {UserId}]";
    }

    public sealed record SignupClosed : ProvisionOutcome;

    public sealed record Failed(LoginFailure Failure) : ProvisionOutcome;
}

public sealed class Provisioner(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    OwnerClaim ownerClaim,
    TimeProvider timeProvider)
{
    private const int MaxRuns = 3;
    private const string FirstTenantName = "Recall";

    private static readonly string[] RaceConstraints = ["ix_tenants_name", "pk_auth_external_logins", "ix_users_identifier"];

    private readonly ILogger _logger = Log.ForContext<Provisioner>();

    public async Task<ProvisionOutcome> ResolveAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var claim = new ClaimOfThisLogin(ownerClaim);
        for (var run = 1; ; run++)
        {
            await using var scope = scopeFactory.CreateSystemScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                return await ResolveOnceAsync(dbContext, identity, claim, cancellationToken);
            }
            catch (Exception exception) when (IsLostRace(exception))
            {
                if (run == MaxRuns)
                {
                    _logger.Here().Warning("A login lost {Runs} races in a row and stopped", MaxRuns);
                    return new ProvisionOutcome.Failed(LoginFailure.InvalidResponse);
                }
            }
        }
    }

    private static bool IsLostRace(Exception exception)
        => exception is StaleVersionException
            || (exception is ConstraintConflictException conflict && RaceConstraints.Contains(conflict.Constraint));

    private async Task<ProvisionOutcome> ResolveOnceAsync(
        AppDbContext dbContext, ExternalIdentity identity, ClaimOfThisLogin claim, CancellationToken cancellationToken)
    {
        var link = await dbContext.Set<ExternalLogin>()
            .TagWithOperationCallSite("auth.provision.find_link")
            .FirstOrDefaultAsync(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject, cancellationToken);
        if (link is not null)
        {
            // This closes the claim, so a file that the operator made and then used through an old link cannot wait for a stranger.
            if (claim.Take())
            {
                // A rerun after a lost race finds nothing left to give, and that is not a claim that changed nothing.
                claim.GaveBack |= await GiveBackToLinkedAdminAsync(dbContext, link.UserId, cancellationToken);
                if (!claim.GaveBack)
                    _logger.Here().Warning(
                        "A login with a link removed the owner claim file {ClaimFile}, and the claim changed nothing. To claim the Owner, create the file again and sign in with the new identity",
                        ownerClaim.FilePath);
            }

            return await SignInLinkedAsync(dbContext, link, identity.Profile, cancellationToken);
        }

        var tenants = await dbContext.Tenants
            .TagWithOperationCallSite("auth.provision.tenants_ever")
            .IgnoreQueryFilters()
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.IsDeleted })
            .ToListAsync(cancellationToken);
        var anyTenantEver = tenants.Count > 0;
        var oldestTenantId = tenants.FirstOrDefault(t => !t.IsDeleted)?.Id;

        if (claim.Take() && oldestTenantId is not null)
            return await ClaimOwnerAsync(dbContext, identity, oldestTenantId, cancellationToken);

        if (!anyTenantEver)
            return await AddFirstOwnerAsync(dbContext, identity, cancellationToken);

        if (options.Value.OpenSignup && oldestTenantId is not null)
            return await JoinAsync(dbContext, identity, oldestTenantId, [], cancellationToken);

        return new ProvisionOutcome.SignupClosed();
    }

    private async Task<ProvisionOutcome> SignInLinkedAsync(
        AppDbContext dbContext, ExternalLogin link, Profile profile, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .TagWithOperationCallSite("auth.provision.linked_user")
            .FirstOrDefaultAsync(u => u.Id == link.UserId, cancellationToken);
        if (user is null)
            return new ProvisionOutcome.SignupClosed();

        link.LastLoginAt = timeProvider.GetUtcNow();

        var lastName = user.LastName ?? NewPerson.LastNameOf(profile);
        var email = user.Email?.Value ?? NewPerson.EmailOf(profile);
        if (lastName != user.LastName || email != user.Email?.Value)
            user.UpdateFromIdp(user.FirstName, lastName, email, user.Username);

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ProvisionOutcome.Provisioned(user.Id, user.FullName);
    }

    private async Task<ProvisionOutcome> ClaimOwnerAsync(
        AppDbContext dbContext, ExternalIdentity identity, string tenantId, CancellationToken cancellationToken)
    {
        var linked = await RootLock.Tenant.RunSharedAsync<ProvisionOutcome?>(dbContext, tenantId, async ct =>
        {
            var admin = await ClaimedAdminAsync(dbContext, tenantId, ct);
            if (admin is null)
                return null;

            GiveBackEachPermission(dbContext, admin);
            dbContext.Set<ExternalLogin>().Add(LinkOf(identity, admin.Id));
            await dbContext.SaveChangesAsync(ct);
            _logger.Here().Warning("The owner claim file linked a login of issuer {Issuer} to user {UserId} and gave back each Admin permission", identity.Issuer, admin.Id);
            return new ProvisionOutcome.Provisioned(admin.Id, admin.FullName);
        }, cancellationToken);

        return linked ?? await JoinAsync(dbContext, identity, tenantId, [UserRole.Admin()], cancellationToken);
    }

    /// <summary>A permission that a later release adds reaches no Admin, and only a claim recovers that when no Admin holds each one.</summary>
    private async Task<bool> GiveBackToLinkedAdminAsync(AppDbContext dbContext, string userId, CancellationToken cancellationToken)
    {
        var tenantId = await dbContext.Users
            .TagWithOperationCallSite("auth.provision.linked_tenant")
            .Where(u => u.Id == userId)
            .Select(u => u.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null)
            return false;

        return await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
        {
            var admin = await dbContext.Users
                .TagWithOperationCallSite("auth.provision.linked_admin")
                .Include(u => u.UserPermissions)
                .Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.Id == userId && u.Roles.Any(r => r.Role.Value == UserRole.AdminName), ct);
            if (admin is null || !GiveBackEachPermission(dbContext, admin))
                return false;

            await dbContext.SaveChangesAsync(ct);
            _logger.Here().Warning("The owner claim file {ClaimFile} gave back each Admin permission to the linked user {UserId}", ownerClaim.FilePath, admin.Id);
            return true;
        }, cancellationToken);
    }

    /// <summary>The tenant of an owner claim: the oldest live tenant, as <see cref="ResolveOnceAsync"/> reads it.</summary>
    internal static Task<string?> ClaimedTenantIdAsync(AppDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.Tenants
            .TagWithOperationCallSite("auth.provision.oldest_tenant")
            .IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The Admin that an owner claim picks in its tenant: the oldest live Admin whose identifier is its own ID.</summary>
    internal static Task<User?> ClaimedAdminAsync(AppDbContext dbContext, string tenantId, CancellationToken cancellationToken)
        => dbContext.Users
            .TagWithOperationCallSite("auth.provision.oldest_admin")
            .Include(u => u.UserPermissions)
            .Include(u => u.Roles)
            .OwnerFirstIn(tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Gives the Admin back each removed permission, as the owner claim does, and says if it gave one.</summary>
    internal static bool GiveBackEachPermission(AppDbContext dbContext, User admin)
    {
        var removed = admin.UserPermissions.Where(p => p.Effect == PermissionEffect.Deny).Select(p => p.Permission).ToList();
        foreach (var permission in removed)
            dbContext.Track(admin.AddPermission(permission));
        return removed.Count > 0;
    }

    private async Task<ProvisionOutcome> AddFirstOwnerAsync(
        AppDbContext dbContext, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var tenant = Tenant.Create(new TenantForCreation { Name = FirstTenantName });
        var user = User.CreateFromLogin(NewPerson.FromClaims(identity.Profile), tenant.Id, [UserRole.Admin()]);
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        dbContext.Set<ExternalLogin>().Add(LinkOf(identity, user.Id));
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.Here().Information("The first login made tenant {TenantId} and its Owner {UserId}", tenant.Id, user.Id);
        return new ProvisionOutcome.Provisioned(user.Id, user.FullName);
    }

    private async Task<ProvisionOutcome> JoinAsync(
        AppDbContext dbContext, ExternalIdentity identity, string tenantId, IReadOnlyList<UserRole> roles, CancellationToken cancellationToken)
    {
        return await RootLock.Tenant.RunSharedAsync<ProvisionOutcome>(dbContext, tenantId, async ct =>
        {
            var tenantIsLive = await dbContext.Tenants
                .TagWithOperationCallSite("auth.provision.tenant_is_live")
                .AnyAsync(t => t.Id == tenantId, ct);
            if (!tenantIsLive)
                return new ProvisionOutcome.SignupClosed();

            var user = User.CreateFromLogin(NewPerson.FromClaims(identity.Profile), tenantId, roles);
            dbContext.Users.Add(user);
            dbContext.Set<ExternalLogin>().Add(LinkOf(identity, user.Id));
            await dbContext.SaveChangesAsync(ct);

            // Only a claim adds an Admin here, and the operator must be able to find who got it.
            var level = roles.Contains(UserRole.Admin()) ? LogEventLevel.Warning : LogEventLevel.Information;
            _logger.Here().Write(level, "A login added user {UserId} to tenant {TenantId} with roles {Roles}", user.Id, tenantId, roles.Select(r => r.Value).ToList());
            return new ProvisionOutcome.Provisioned(user.Id, user.FullName);
        }, cancellationToken);
    }

    private ExternalLogin LinkOf(ExternalIdentity identity, string userId)
    {
        var now = timeProvider.GetUtcNow();
        return new ExternalLogin
        {
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            UserId = userId,
            CreatedAt = now,
            LastLoginAt = now,
            DisplayLabel = NewPerson.LabelOf(identity.Profile),
        };
    }

    private sealed class ClaimOfThisLogin(OwnerClaim ownerClaim)
    {
        private bool _won;

        public bool GaveBack { get; set; }

        public bool Take() => _won = _won || ownerClaim.TryConsume();
    }
}
