namespace Recall.Server.Domain.Users;

using Domain.Tenants;
public abstract record CallerIdentity
{
    private CallerIdentity() { }

    /// <summary>System work in a scope from <see cref="SystemScopeExtensions.CreateSystemScope"/>.</summary>
    public sealed record System : CallerIdentity;

    /// <summary>
    /// A client on <see cref="TenancyOptions.ReadAllTenantsClientIds"/> or
    /// <see cref="TenancyOptions.ManageAllTenantsClientIds"/>, whatever the <c>sub</c> of the token.
    /// </summary>
    public sealed record AllTenantsClient(string ClientId, bool CanManage) : CallerIdentity;

    public sealed record User(string Subject, LiveUser? LiveUser) : CallerIdentity;

    /// <summary>Work for one journal owner in a scope from <see cref="SystemScopeExtensions.CreateJournalScope"/>, such as a job.</summary>
    public sealed record JournalWork(string UserId, string TenantId) : CallerIdentity;

    /// <summary>
    /// A client that the tenant of the user registered as a delegated agent, in a token for a live app user of that
    /// tenant.
    /// </summary>
    public sealed record DelegatedAgent(string AgentId, string ClientId, string Subject, LiveUser LiveUser)
        : CallerIdentity;

    /// <summary>
    /// A client registered as a machine agent, whatever the <c>sub</c> of the token. The token names no tenant, so the
    /// agent gives the tenant.
    /// </summary>
    public sealed record MachineAgent(string AgentId, string ClientId, string TenantId) : CallerIdentity;

    /// <summary>
    /// A personal access token of a live app user. It acts as the user, inside the grant fixed when the token was made.
    /// </summary>
    public sealed record PersonalAccessToken(string TokenId, string Subject, LiveUser LiveUser, TokenGrant Grant)
        : CallerIdentity;

    /// <summary>
    /// A client that is not first-party, not on an allow-list, and not registered for this token. Also a token with
    /// no client claim. <paramref name="ClientId"/> is null for such a token.
    /// </summary>
    public sealed record UnknownClient(string? ClientId) : CallerIdentity;

    /// <summary>A request with no authenticated principal.</summary>
    public sealed record Anonymous : CallerIdentity;

    /// <summary>The tenants whose rows the caller sees.</summary>
    public TenantScope Scope => this switch
    {
        System or AllTenantsClient => TenantScope.AllTenants,
        User { LiveUser: { } liveUser } => TenantScope.ForTenant(liveUser.TenantId),
        JournalWork work => TenantScope.ForTenant(work.TenantId),
        DelegatedAgent agent => TenantScope.ForTenant(agent.LiveUser.TenantId),
        MachineAgent agent => TenantScope.ForTenant(agent.TenantId),
        PersonalAccessToken token => TenantScope.ForTenant(token.LiveUser.TenantId),
        _ => TenantScope.NoTenant,
    };

    public string? JournalOwnerId => this switch
    {
        User { LiveUser: { } liveUser } => liveUser.UserId,
        JournalWork work => work.UserId,
        PersonalAccessToken { Grant.Journal.CanRead: true } token => token.LiveUser.UserId,
        _ => null,
    };

    /// <summary>
    /// True for a caller that may add, change, or delete rows of its journal. A personal access token needs
    /// <see cref="JournalAccess.ReadWrite"/>.
    /// </summary>
    public bool CanWriteJournal => this is User { LiveUser: not null } or JournalWork
        or PersonalAccessToken { Grant.Journal.CanWrite: true };

    public JournalAccess Journal => JournalOwnerId is null ? JournalAccess.None
        : CanWriteJournal ? JournalAccess.ReadWrite
        : JournalAccess.Read;

    /// <summary>
    /// True for system work and for a client on the manage allow-list. It is a capability, not a permission
    /// (ADR 0009).
    /// </summary>
    public bool CanManageAllTenants => this is System or AllTenantsClient { CanManage: true };
}
