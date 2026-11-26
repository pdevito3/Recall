namespace Recall.Server.Domain.Users;

using Databases;

/// <summary>
/// The role and override rows of a user have IDs before the save, and the navigations use field access, so EF would
/// take a new row for a changed one. Each change of a role or an override tells the context about its rows here.
/// </summary>
public static class UserRowTracking
{
    public static void Track(this AppDbContext dbContext, OverrideChange change)
    {
        if (change.Removed is { } removed)
            dbContext.UserPermissions.Remove(removed);
        if (change.Added is { } added)
            dbContext.UserPermissions.Add(added);
    }

    public static void Track(this AppDbContext dbContext, RoleChange change)
    {
        if (change.Added is { } added)
            dbContext.UserRoles.Add(added);
        if (change.Removed is { } removed)
            dbContext.UserRoles.Remove(removed);
        dbContext.UserPermissions.RemoveRange(change.ClearedOverrides);
    }
}
