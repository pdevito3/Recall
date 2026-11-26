namespace Recall.Server.Domain.Users;

/// <summary>
/// The one rule that turns the roles and the overrides of a user into the permissions that the user holds: each
/// permission of each role, with each added permission, less each removed permission.
/// </summary>
public static class EffectivePermissions
{
    public static IReadOnlySet<Permission> FromRoles(IEnumerable<UserRole> roles)
        => roles.SelectMany(role => role.Permissions()).ToHashSet();

    public static IReadOnlySet<Permission> Of(
        IEnumerable<UserRole> roles, IEnumerable<Permission> added, IEnumerable<Permission> removed)
    {
        var held = FromRoles(roles).ToHashSet();
        held.UnionWith(added);
        held.ExceptWith(removed);
        return held;
    }
}
