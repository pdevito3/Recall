namespace Recall.Server.Domain.Users;

public static class OwnerQuery
{
    /// <summary>
    /// The Admins of a tenant, with the Owner first. No row marks the Owner. The Owner is the oldest Admin of the oldest
    /// live tenant, and a user ID grows with time.
    /// </summary>
    public static IQueryable<User> OwnerFirstIn(this IQueryable<User> users, string tenantId)
    {
        var adminRole = UserRole.Admin().Value;
        return users
            .Where(u => u.TenantId == tenantId && u.Role.Value == adminRole && u.Identifier == u.Id)
            .OrderBy(u => u.Id);
    }
}
