namespace Recall.Server.Domain.Users;

public static class OwnerQuery
{
    // No row marks the Owner. The oldest Admin of the oldest live tenant stands for the first login, and a user ID grows with time.
    public static IQueryable<User> OwnerFirstIn(this IQueryable<User> users, string tenantId)
    {
        var adminRole = UserRole.Admin().Value;
        return users
            .Where(u => u.TenantId == tenantId && u.Role.Value == adminRole && u.Identifier == u.Id)
            .OrderBy(u => u.Id);
    }
}
