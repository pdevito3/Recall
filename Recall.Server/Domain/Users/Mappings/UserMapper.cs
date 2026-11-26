namespace Recall.Server.Domain.Users.Mappings;

using System.Diagnostics.CodeAnalysis;
using Auth;
using Dtos;
using EmailAddresses;
using Models;
using Riok.Mapperly.Abstractions;

[Mapper]
public static partial class UserMapper
{
    /// <summary>The DTO with the actions that <paramref name="caller"/> can send for the user, and the login name of its password credential.</summary>
    public static UserDto ToUserDto(this User user, Caller caller, LoginName? loginName)
    {
        var dto = user.ToUserDtoWithoutActions();
        return dto with
        {
            Roles = InOneOrder(dto.Roles),
            Permissions = InOneOrder(user.Permissions().Select(p => p.Value)),
            AddedPermissions = InOneOrder(dto.AddedPermissions),
            RemovedPermissions = InOneOrder(dto.RemovedPermissions),
            AllowedActions = NamesOf(user.AllowedActions(caller)),
            AddableRoles = user.AddableRoles(caller).Select(r => r.Value).ToList(),
            LoginName = loginName?.Value,
        };
    }

    /// <summary>
    /// Gives each row of a list the actions that <paramref name="caller"/> can send for it, and its login name. The list
    /// query projects to the DTO, so the rule reads the ID, the roles, and the overrides of the DTO. The roles give
    /// their permissions in code, not in SQL, so the permissions come here too.
    /// </summary>
    public static void AddAllowedActions(this List<UserDto> users, Caller caller, IReadOnlyDictionary<string, LoginName> loginNames)
    {
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var permissions = EffectivePermissions.Of(
                user.Roles.Select(UserRole.Of), user.AddedPermissions.Select(Permission.Of), user.RemovedPermissions.Select(Permission.Of))
                .ToArray();
            users[i] = user with
            {
                Roles = InOneOrder(user.Roles),
                Permissions = InOneOrder(permissions.Select(p => p.Value)),
                AddedPermissions = InOneOrder(user.AddedPermissions),
                RemovedPermissions = InOneOrder(user.RemovedPermissions),
                AllowedActions = NamesOf(UserAction.AllowedFor(caller, user.Id, permissions)),
                AddableRoles = UserRole.AddableBy(caller, user.Id, permissions, user.Roles).Select(r => r.Value).ToList(),
                LoginName = loginNames.GetValueOrDefault(users[i].Id)?.Value,
            };
        }
    }

    // It takes the configuration of ToUserDtoWithoutActions. The list handler adds the actions after the page loads.
    public static partial IQueryable<UserDto> ToUserDtoQueryable(this IQueryable<User> queryable);

    // The handler decides the tenant from the caller's scope. The tenant in the DTO is only a request.
    // The handler also mints the ID, and the identifier is that ID, so that a session of the user finds the user.
    [MapperIgnoreSource(nameof(UserForCreationDto.TenantId))]
    [MapperIgnoreTarget(nameof(UserForCreation.Id))]
    [MapperIgnoreTarget(nameof(UserForCreation.Identifier))]
    public static partial UserForCreation ToUserForCreation(this UserForCreationDto dto, string tenantId);

    public static partial UserForUpdate ToUserForUpdate(this UserForUpdateDto dto);

    [MapperIgnoreSource(nameof(User.LastModifiedOn))]
    [MapperIgnoreSource(nameof(User.IsDeleted))]
    [MapperIgnoreSource(nameof(User.DomainEvents))]
    [MapperIgnoreTarget(nameof(UserDto.AllowedActions))]
    [MapperIgnoreTarget(nameof(UserDto.AddableRoles))]
    [MapperIgnoreTarget(nameof(UserDto.LoginName))]
    [MapperIgnoreSource(nameof(User.UserPermissions))]
    [MapperIgnoreTarget(nameof(UserDto.Permissions))]
    [MapProperty(nameof(User.Roles), nameof(UserDto.Roles))]
    [MapPropertyFromSource(nameof(UserDto.AddedPermissions), Use = nameof(MapAddedPermissions))]
    [MapPropertyFromSource(nameof(UserDto.RemovedPermissions), Use = nameof(MapRemovedPermissions))]
    [MapPropertyFromSource(nameof(UserDto.FullName), Use = nameof(MapFullName))]
    private static partial UserDto ToUserDtoWithoutActions(this User user);

    private static List<string> NamesOf(IEnumerable<UserAction> actions) => actions.Select(a => a.Value).ToList();
    // The projection inlines the list maps into SQL, so the order comes after the rows load.
    private static List<string> InOneOrder(IEnumerable<string> names) => names.Order(StringComparer.Ordinal).ToList();
    private static string MapEmail(EmailAddress email) => email.Value;
    // The projection inlines this expression, so EF can filter and sort on FullName. User.FullName is not a column.
    private static string MapFullName(User user) => user.LastName == null ? user.FirstName : user.FirstName + " " + user.LastName;
    // Mapperly uses these methods only when their return type is the type of the target list.
    // With List<string>, Mapperly ignores them and maps each item with ToString().
    [SuppressMessage("Performance", "CA1859", Justification = "Mapperly matches on the exact target type.")]
    private static IReadOnlyList<string> MapRoles(IReadOnlyCollection<UserRoleAssignment> roles)
        => roles.Select(r => r.Role.Value).ToList();
    [SuppressMessage("Performance", "CA1859", Justification = "Mapperly matches on the exact target type.")]
    private static IReadOnlyList<string> MapAddedPermissions(User user)
        => user.UserPermissions.Where(p => p.Effect == PermissionEffect.Grant).Select(p => p.Permission.Value).ToList();
    [SuppressMessage("Performance", "CA1859", Justification = "Mapperly matches on the exact target type.")]
    private static IReadOnlyList<string> MapRemovedPermissions(User user)
        => user.UserPermissions.Where(p => p.Effect == PermissionEffect.Deny).Select(p => p.Permission.Value).ToList();
}
