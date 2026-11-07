namespace Recall.Server.Domain.Tenants;

using Databases;

/// <summary>
/// Marks one DI scope as system work, or as work for one journal owner. In a system scope,
/// <see cref="TenantScopeProvider"/> gives <see cref="TenantScope.AllTenants"/> and lets the caller manage all tenants.
/// Only <see cref="SystemScopeExtensions.CreateSystemScope"/> and <see cref="SystemScopeExtensions.CreateJournalScope"/>
/// set it, when they create the scope.
/// </summary>
public sealed class SystemTenantAccess
{
    public bool IsGranted { get; private set; }

    public JournalOwner? JournalOwner { get; private set; }

    internal void Grant() => IsGranted = true;

    internal void GrantJournal(JournalOwner owner) => JournalOwner = owner;
}

public static class SystemScopeExtensions
{
    /// <summary>
    /// Creates a DI scope for system work that must see and manage all tenants, for example seeding,
    /// a background job, or the REPL. The grant applies to this scope only. It does not flow into
    /// other scopes or into work that this scope starts.
    /// </summary>
    /// <remarks>
    /// A scope from <see cref="IServiceScopeFactory.CreateScope"/> with no HTTP request gets
    /// <see cref="TenantScope.NoTenant"/>. All-tenant access must be an explicit decision.
    /// </remarks>
    public static AsyncServiceScope CreateSystemScope(this IServiceScopeFactory scopeFactory)
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SystemTenantAccess>().Grant();
        return scope;
    }

    /// <summary>
    /// A job has no caller, and a system scope sees no journal, because no one reads the journal of another person. This
    /// scope sees the rows of the owner and no others, with the query filters on. The IDs come from the payload that the
    /// request of the owner enqueued, or from the rows of the sweep.
    /// </summary>
    public static AsyncServiceScope CreateJournalScope(this IServiceScopeFactory scopeFactory, JournalOwner owner)
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SystemTenantAccess>().GrantJournal(owner);
        return scope;
    }
}
