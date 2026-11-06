namespace Recall.Server.Databases;

using Exceptions;

public sealed record JournalOwner(string UserId, string TenantId);

public static class JournalOwnerExtensions
{
    public static JournalOwner RequireJournalOwner(this AppDbContext dbContext)
        => dbContext.CurrentJournalOwnerId is { } userId && dbContext.CurrentTenantScope.TenantId is { } tenantId
            ? new JournalOwner(userId, tenantId)
            : throw new ForbiddenAccessException("Only the person who owns a journal can use it.");
}
