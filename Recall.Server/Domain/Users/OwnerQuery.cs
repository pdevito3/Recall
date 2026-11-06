namespace Recall.Server.Domain.Users;

public static class OwnerQuery
{
    public static IQueryable<User> OwnerFirstIn(this IQueryable<User> users, string tenantId)
    {
        var adminRole = UserRole.Admin().Value;
        return users
            .Where(u => u.TenantId == tenantId && u.Role.Value == adminRole && u.Identifier == u.Id)
            .OrderBy(u => u.Id);
    }
}
