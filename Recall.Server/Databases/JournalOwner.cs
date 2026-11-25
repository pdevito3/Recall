namespace Recall.Server.Databases;

using Domain.Users;
using Exceptions;

public sealed record JournalOwner(string UserId, string TenantId);

public static class JournalOwnerExtensions
{
    internal const string NoJournalMessage = "Only the person who owns a journal can use it.";

    public static JournalOwner RequireJournalOwner(this AppDbContext dbContext)
        => dbContext.CurrentJournalOwnerId is { } userId && dbContext.CurrentTenantScope.TenantId is { } tenantId
            ? new JournalOwner(userId, tenantId)
            : throw new ForbiddenAccessException(NoJournalMessage);

    /// <summary>
    /// Refuses a caller that reads the journal but cannot change it. Call it before any effect outside the save, such
    /// as a file, a job, or a schedule. <see cref="JournalWriteRule"/> refuses the save too, but only after that effect.
    /// A caller with no journal passes, so the <c>JournalOwner</c> filter still gives it a 404 or a 403.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The caller reads the journal and cannot change it.</exception>
    public static void RequireJournalWriter(this AppDbContext dbContext)
    {
        if (dbContext.CurrentJournalAccess == JournalAccess.Read)
            throw new ForbiddenAccessException(JournalWriteRule.Refusal);
    }
}
