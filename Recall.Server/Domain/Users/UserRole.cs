namespace Recall.Server.Domain.Users;

using Ardalis.SmartEnum;
using Exceptions;

/// <summary>
/// A named set of permissions. A user holds any number of roles, and the permissions of each role add up, with the
/// overrides of the user on top (see <see cref="EffectivePermissions"/>). A user with no role is a Member: the full use
/// of their own journal, which needs no permission, and no admin pages.
/// </summary>
public partial class UserRole : ValueObject, IFixedNames
{
    // The key in the errors of the problem is the name of the request field
    private const string RequestField = "role";

    private UserRoleEnum _role = null!;

    public string Value
    {
        get => _role.Name;
        private set => _role = Parse(value, RequestField);
    }

    public UserRole(string value)
    {
        Value = value;
    }

    /// <summary>
    /// The permissions of the role. The code holds them, so a permission that a release adds to a role reaches each
    /// user with the role.
    /// </summary>
    public IReadOnlyList<Permission> Permissions() => _role.Permissions;

    public override string ToString() => Value;

    public static UserRole Of(string value) => new(value);

    /// <summary>The role of a request field with another name, for example <c>roles[1]</c> for an item of a list.</summary>
    public static UserRole Of(string value, string requestField) => new() { _role = Parse(value, requestField) };

    public static implicit operator string(UserRole value) => value.Value;
    public static List<string> ListNames() => UserRoleEnum.List.Select(x => x.Name).ToList();

    /// <summary>The name of the role Admin, for a query that EF turns into SQL.</summary>
    public const string AdminName = "Admin";

    /// <summary>
    /// The roles that the user does not hold and the caller can add: the caller may send AddRole for the user and holds
    /// each permission of the role (ADR 0009). Empty when the caller cannot add a role.
    /// </summary>
    internal static IReadOnlyList<UserRole> AddableBy(
        Caller caller, string userId, IReadOnlyCollection<Permission> userPermissions, IEnumerable<string> heldRoles)
    {
        if (!UserAction.AllowedFor(caller, userId, userPermissions).Contains(UserAction.AddRole()))
            return [];
        var held = heldRoles.ToHashSet(StringComparer.Ordinal);
        return UserRoleEnum.List
            .OrderBy(r => r.Value)
            .Where(r => !held.Contains(r.Name) && r.Permissions.All(caller.Has))
            .Select(r => new UserRole { _role = r })
            .ToList();
    }

    public static UserRole Admin() => new(UserRoleEnum.Admin.Name);
    public static UserRole UserManager() => new(UserRoleEnum.UserManager.Name);
    public static UserRole Auditor() => new(UserRoleEnum.Auditor.Name);

    protected UserRole() { } // EF Core

    private static UserRoleEnum Parse(string? value, string requestField)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException(requestField, "Role cannot be null or empty.");

        return UserRoleEnum.TryFromName(value, true, out var parsed)
            ? parsed
            : throw new ValidationException(requestField, $"Invalid role: {value}");
    }

    private sealed class UserRoleEnum(string name, int value, Func<IReadOnlyList<Permission>> permissions)
        : SmartEnum<UserRoleEnum>(name, value)
    {
        // Each permission, also one that a later release adds. LastAdmin relies on this: an Admin with no removed
        // permission holds each permission.
        public static readonly UserRoleEnum Admin = new(AdminName, 0, () => Permission.GetAll());

        public static readonly UserRoleEnum UserManager = new("UserManager", 1,
            () => [Permission.ManageUsers(), Permission.ManageUserAccess(), Permission.ReadUsers()]);

        public static readonly UserRoleEnum Auditor = new("Auditor", 2,
            () => [Permission.ReadAuditLog(), Permission.ReadAuditLogSensitive(), Permission.ReadUsers(), Permission.ReadAgents()]);

        private readonly Lazy<IReadOnlyList<Permission>> _permissions = new(permissions);

        public IReadOnlyList<Permission> Permissions => _permissions.Value;
    }
}
