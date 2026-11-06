namespace Recall.Server.Domain.Users;

/// <summary>
/// Marks one DI scope as work for one person who owns a journal, such as a job that the person started. The scope sees
/// the rows of this person and no others, with the query filters on.
/// Only <see cref="JournalWorkScopeExtensions.CreateJournalScope"/> sets it, when it creates the scope.
/// </summary>
public sealed class JournalWorkAccess
{
    public (string TenantId, string UserId)? Grant { get; private set; }

    internal void Give(string tenantId, string userId) => Grant = (tenantId, userId);
}

public static class JournalWorkScopeExtensions
{
    /// <summary>
    /// A job has no caller, and a system scope sees no journal, because no one reads the journal of another person. The
    /// IDs come from the payload that the request of the owner enqueued.
    /// </summary>
    public static AsyncServiceScope CreateJournalScope(this IServiceScopeFactory scopeFactory, string tenantId, string userId)
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JournalWorkAccess>().Give(tenantId, userId);
        return scope;
    }
}
