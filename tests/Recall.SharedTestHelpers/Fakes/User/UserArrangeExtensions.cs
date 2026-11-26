namespace Recall.SharedTestHelpers.Fakes.User;

using Recall.Server.Domain.Users;

/// <summary>Arranges a user that no context tracks yet, so the rows of the change need no tracking.</summary>
public static class UserArrangeExtensions
{
    /// <summary>Gives the user each permission, on top of the roles, before the first save.</summary>
    public static User AddPermissions(this User user, IEnumerable<Permission> permissions)
    {
        foreach (var permission in permissions)
            user.AddPermission(permission);
        return user;
    }
}
