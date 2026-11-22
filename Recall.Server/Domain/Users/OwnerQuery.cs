namespace Recall.Server.Domain.Users;

public static class OwnerQuery
{
    // No row marks the Owner. The oldest Admin whose identifier is its ID stands for the Owner: a first login, a setup, an Admin added through the API, or the first Admin of a tenant that initiate starts.
    // A user ID grows with time.
    public static IQueryable<User> OwnerFirstIn(this IQueryable<User> users, string tenantId)
    {
        var adminRole = UserRole.Admin().Value;
        return users
            .Where(u => u.TenantId == tenantId && u.Role.Value == adminRole && u.Identifier == u.Id)
            .OrderBy(u => u.Id);
    }
}
