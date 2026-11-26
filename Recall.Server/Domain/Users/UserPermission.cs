namespace Recall.Server.Domain.Users;

/// <summary>
/// An override of one permission for one user, on top of the roles of the user. A Grant adds a permission that no role
/// of the user gives. A Deny removes a permission that a role of the user gives. A user has at most one live row for
/// each permission.
/// </summary>
public class UserPermission : BaseEntity
{
    public const string IdPrefix = "uperm";

    [NotSensitive] public string UserId { get; private set; } = default!;
    public User User { get; private set; } = default!;
    [NotSensitive] public Permission Permission { get; private set; } = default!;
    public PermissionEffect Effect { get; private set; }

    internal static UserPermission Create(User user, Permission permission, PermissionEffect effect)
        => new() { UserId = user.Id, Permission = permission, Effect = effect };

    protected UserPermission() : base(IdPrefix) { } // EF Core
}

public enum PermissionEffect
{
    Grant,
    Deny,
}
