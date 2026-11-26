namespace Recall.Server.Domain.Users;

using System.Diagnostics;
using Databases;
using Exceptions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The one way for a handler to require a permission of the current caller in the current tenant.
/// Call it as the first statement of the handler, before any load, so a caller without the permission
/// gets a 403 for any ID.
/// </summary>
public interface IPermissionGuard
{
    /// <summary>
    /// Loads the caller. Use it when a rule needs <see cref="Caller.Permissions"/>, for example an entity rule that
    /// depends on the state of the entity, or when the handler makes more than one check. To make one check, use
    /// <see cref="RequireAsync(Permission, CancellationToken)"/>.
    /// </summary>
    Task<Caller> GetCallerAsync(CancellationToken cancellationToken);

    /// <exception cref="ForbiddenAccessException">The caller does not hold the permission.</exception>
    Task RequireAsync(Permission permission, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the caller and requires a person signed in to Recall, in a web or native session. Use it for an action
    /// that must come from the person, such as the create of a personal access token, so that a token cannot make a
    /// token.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">
    /// The caller is a provider token, a personal access token, an agent, or another client.
    /// </exception>
    Task<Caller.Member> RequireSignedInPersonAsync(CancellationToken cancellationToken);
}

public sealed class PermissionGuard(
    AppDbContext dbContext,
    ICallerIdentityResolver callerIdentityResolver,
    ICurrentUserService currentUserService) : IPermissionGuard
{
    public async Task RequireAsync(Permission permission, CancellationToken cancellationToken)
        => (await GetCallerAsync(cancellationToken)).Require(permission);

    // Only the AppSession scheme gives the client recall to a web or native session. A provider token that names it
    // gets 401, and a personal access token has its token ID as its client.
    public async Task<Caller.Member> RequireSignedInPersonAsync(CancellationToken cancellationToken)
        => await GetCallerAsync(cancellationToken) is Caller.Member member
            && currentUserService.ClientId == FirstPartyClientOptions.AppClientId
                ? member
                : throw new ForbiddenAccessException("Only a person signed in to Recall can do this.");

    public async Task<Caller> GetCallerAsync(CancellationToken cancellationToken)
        => await callerIdentityResolver.GetAsync(cancellationToken) switch
        {
            { CanManageAllTenants: true } => Caller.AllTenantsManager.Instance,
            CallerIdentity.AllTenantsClient => new Caller.Outsider(OutsiderKind.MachineClient),
            CallerIdentity.User user => await LoadUserAsync(user.Subject, cancellationToken) is { } member
                ? new Caller.Member(member.Id, member.Permissions())
                : new Caller.Outsider(OutsiderKind.NoUserRecord),
            CallerIdentity.DelegatedAgent agent => await LoadDelegatedAgentAsync(agent, cancellationToken),
            CallerIdentity.PersonalAccessToken token => await LoadUserAsync(token.Subject, cancellationToken) is { } user
                ? new Caller.PersonalAccessToken(token.TokenId, user.Id, user.Permissions(), token.Grant)
                : new Caller.Outsider(OutsiderKind.NoUserRecord),
            CallerIdentity.MachineAgent agent => await LoadGrantAsync(agent.AgentId, cancellationToken) is { } grant
                ? new Caller.MachineAgent(agent.ClientId, grant)
                : new Caller.Outsider(OutsiderKind.UnknownClient),
            CallerIdentity.JournalWork or CallerIdentity.UnknownClient => new Caller.Outsider(OutsiderKind.UnknownClient),
            CallerIdentity.Anonymous => new Caller.Outsider(OutsiderKind.Anonymous),
            _ => throw new UnreachableException(),
        };

    private async Task<Caller> LoadDelegatedAgentAsync(
        CallerIdentity.DelegatedAgent agent, CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(agent.Subject, cancellationToken) is not { } user)
            return new Caller.Outsider(OutsiderKind.NoUserRecord);

        if (await LoadGrantAsync(agent.AgentId, cancellationToken) is not { } grant)
            return new Caller.Outsider(OutsiderKind.UnknownClient);

        return new Caller.DelegatedAgent(agent.ClientId, user.Permissions(), grant);
    }

    private Task<User?> LoadUserAsync(string identifier, CancellationToken cancellationToken)
        => dbContext.Users
            .TagWithOperationCallSite("users.permission_guard.load")
            .AsNoTracking()
            .Include(u => u.UserPermissions)
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Identifier == identifier, cancellationToken);

    private async Task<IEnumerable<Permission>?> LoadGrantAsync(string agentId, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Agents
            .TagWithOperationCallSite("agents.permission_guard.load")
            .AsNoTracking()
            .Include(a => a.Permissions)
            .Where(a => a.Id == agentId && dbContext.Tenants.Any(t => t.Id == a.TenantId))
            .FirstOrDefaultAsync(cancellationToken);

        return agent?.Permissions.Select(p => p.Permission);
    }
}
