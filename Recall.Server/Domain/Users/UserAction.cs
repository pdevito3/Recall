namespace Recall.Server.Domain.Users;

using Ardalis.SmartEnum;
using Exceptions;

/// <summary>
/// A command that a caller can send for a user record. Each action names the permissions that it needs, so
/// <see cref="User.AllowedActions"/> and <see cref="User.GuardAllows"/> read one table and cannot disagree.
/// A permission that the request names, such as the new role, is not in the table. The command checks it.
/// A rule over other rows, such as <see cref="LastAdmin"/>, is not in the table either. The command refuses with a 409, and the UI shows that refusal.
/// </summary>
public partial class UserAction : ValueObject, IFixedNames
{
    private readonly UserActionEnum _action;

    private UserAction(UserActionEnum action) => _action = action;

    public string Value => _action.Name;

    public override string ToString() => Value;

    public static List<string> ListNames() => UserActionEnum.List.Select(x => x.Name).ToList();

    public static UserAction Update() => new(UserActionEnum.Update);
    public static UserAction AddRole() => new(UserActionEnum.AddRole);
    public static UserAction RemoveRole() => new(UserActionEnum.RemoveRole);
    public static UserAction AddPermission() => new(UserActionEnum.AddPermission);
    public static UserAction RemovePermission() => new(UserActionEnum.RemovePermission);
    public static UserAction Delete() => new(UserActionEnum.Delete);
    public static UserAction IssueTemporaryPassword() => new(UserActionEnum.IssueTemporaryPassword);
    public static UserAction ManageExternalLogins() => new(UserActionEnum.ManageExternalLogins);

    /// <summary>
    /// Checks the permissions that need only the ID of the user. Call it before the load, so a caller without them
    /// gets a 403 for any ID. Then call <see cref="User.GuardAllows"/> after the load.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The caller lacks a permission. The message names the first one.</exception>
    public void GuardBeforeLoad(Caller caller, string userId) => caller.Require(_action.CallerNeeds(caller, userId));

    /// <summary>The actions that the caller can send for the user, in the order of the table.</summary>
    internal static IReadOnlyList<UserAction> AllowedFor(
        Caller caller, string userId, IReadOnlyCollection<Permission> userPermissions)
        => UserActionEnum.List
            .OrderBy(action => action.Value)
            .Where(action => action.Needs(caller, userId, userPermissions).All(caller.Has))
            .Select(action => new UserAction(action))
            .ToList();

    /// <exception cref="ForbiddenAccessException">The caller lacks a permission. The message names the first one.</exception>
    internal void Guard(Caller caller, string userId, IReadOnlyCollection<Permission> userPermissions)
        => caller.Require(_action.Needs(caller, userId, userPermissions));

    private sealed class UserActionEnum : SmartEnum<UserActionEnum>
    {
        // The response of each command but Delete carries the record of the user, so another caller also needs
        // read_users (ADR 0009). The user can edit their own record with no permission.
        public static readonly UserActionEnum Update = new(nameof(Update), 0,
            always: [], unlessOwnRecord: [Permission.ManageUsers(), Permission.ReadUsers()], revokesUserPermissions: false);

        public static readonly UserActionEnum AddRole = new(nameof(AddRole), 1,
            always: [Permission.ManageUserAccess()], unlessOwnRecord: [Permission.ReadUsers()], revokesUserPermissions: false);

        public static readonly UserActionEnum RemoveRole = new(nameof(RemoveRole), 2,
            always: [Permission.ManageUserAccess()], unlessOwnRecord: [Permission.ReadUsers()], revokesUserPermissions: true);

        public static readonly UserActionEnum AddPermission = new(nameof(AddPermission), 3,
            always: [Permission.ManageUserAccess()], unlessOwnRecord: [Permission.ReadUsers()], revokesUserPermissions: false);

        // The caller must hold each permission of the user, as for RemoveRole and Delete. Otherwise a user with only
        // manage_user_access could take it away from each admin, and no admin could then take it away from that user (ADR 0009).
        public static readonly UserActionEnum RemovePermission = new(nameof(RemovePermission), 4,
            always: [Permission.ManageUserAccess()], unlessOwnRecord: [Permission.ReadUsers()], revokesUserPermissions: true);

        public static readonly UserActionEnum Delete = new(nameof(Delete), 5,
            always: [Permission.ManageUsers()], unlessOwnRecord: [], revokesUserPermissions: true);

        // The caller can then sign in as the user, so the caller must hold each permission of the user (PWD-14). The
        // answer holds no record of the user, so it needs no read_users.
        public static readonly UserActionEnum IssueTemporaryPassword = new(nameof(IssueTemporaryPassword), 6,
            always: [Permission.ManageUserAccess()], unlessOwnRecord: [], revokesUserPermissions: true);

        // A removal takes a way in from the user, as a role change can, so another caller must hold each permission of the user (IDP-12).
        // The user lists and removes their own links with no permission.
        public static readonly UserActionEnum ManageExternalLogins = new(nameof(ManageExternalLogins), 7,
            always: [], unlessOwnRecord: [Permission.ManageUserAccess()], revokesUserPermissions: true);

        private readonly Permission[] _always;
        private readonly Permission[] _unlessOwnRecord;
        private readonly bool _revokesUserPermissions;

        private UserActionEnum(
            string name, int value, Permission[] always, Permission[] unlessOwnRecord, bool revokesUserPermissions)
            : base(name, value)
        {
            _always = always;
            _unlessOwnRecord = unlessOwnRecord;
            _revokesUserPermissions = revokesUserPermissions;
        }

        public Permission[] CallerNeeds(Caller caller, string userId)
            => caller.IsUser(userId) ? _always : [.. _always, .. _unlessOwnRecord];

        public IEnumerable<Permission> Needs(Caller caller, string userId, IReadOnlyCollection<Permission> userPermissions)
            => _revokesUserPermissions ? CallerNeeds(caller, userId).Concat(userPermissions) : CallerNeeds(caller, userId);
    }
}
