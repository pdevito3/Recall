namespace Recall.Server.Domain.Users;

using System.ComponentModel.DataAnnotations.Schema;
using Auth;
using EmailAddresses;
using DomainEvents;
using Models;

public class User : BaseEntity, ITenantable, IDeletedWithEvent
{
    public const string IdPrefix = "usr";
    public const int FirstNameMaxLength = 200;
    public const int LastNameMaxLength = 200;
    public const int IdentifierMaxLength = 255;
    public const string IdentifierInUseMessage = "A user with this identifier already exists.";
    public const int UsernameMaxLength = 100;

    [NotSensitive] public string TenantId { get; private set; } = default!;
    [Sensitive] public string FirstName { get; private set; } = default!;
    [Sensitive] public string? LastName { get; private set; }
    [Sensitive] public string Identifier { get; private set; } = default!;
    [Sensitive] public EmailAddress? Email { get; private set; }
    [Sensitive] public string Username { get; private set; } = default!;

    private readonly List<UserRoleAssignment> _roles = [];
    /// <summary>The roles of the user. A user with no role is a Member.</summary>
    public IReadOnlyCollection<UserRoleAssignment> Roles => _roles.AsReadOnly();

    private readonly List<UserPermission> _userPermissions = [];
    /// <summary>The overrides of the user on top of <see cref="Roles"/>. See <see cref="Permissions"/>.</summary>
    public IReadOnlyCollection<UserPermission> UserPermissions => _userPermissions.AsReadOnly();

    public string FullName => LastName is null ? FirstName : $"{FirstName} {LastName}";

    /// <summary>The save refuses to delete a user that did not queue <see cref="UserDeleted"/>.</summary>
    [NotMapped]
    internal bool DeleteQueued { get; private set; }

    public static User Create(UserForCreation userForCreation)
    {
        var user = new User
        {
            TenantId = userForCreation.TenantId,
            FirstName = userForCreation.FirstName,
            LastName = userForCreation.LastName,
            Identifier = userForCreation.Identifier,
            Email = EmailOf(userForCreation.Email),
            Username = userForCreation.Username,
        };
        // Set the ID before the roles, because each role copies the user ID.
        if (userForCreation.Id is not null)
            user.SetId(userForCreation.Id);

        foreach (var (name, i) in userForCreation.Roles.Select((name, i) => (name, i)))
        {
            var role = UserRole.Of(name, $"roles[{i}]");
            if (!user.HasRole(role))
                user._roles.Add(UserRoleAssignment.Create(user, role));
        }

        ValidateUser(user);
        user.QueueDomainEvent(new UserCreated(user.Id));

        return user;
    }

    public static User CreateFromLogin(NewPerson person, string tenantId, IReadOnlyList<UserRole> roles)
    {
        var id = EntityId.New(IdPrefix);
        return Create(new UserForCreation
        {
            Id = id,
            TenantId = tenantId,
            FirstName = person.FirstName,
            LastName = person.LastName,
            Identifier = id,
            Email = person.Email,
            Username = person.Username,
            Roles = roles.Select(r => r.Value).ToList(),
        });
    }

    public User Update(UserForUpdate userForUpdate)
    {
        GuardVersion(userForUpdate.Version);
        FirstName = userForUpdate.FirstName;
        LastName = userForUpdate.LastName;
        Email = EmailOf(userForUpdate.Email);
        Username = userForUpdate.Username;

        ValidateUser(this);
        QueueDomainEvent(new UserUpdated(Id));

        return this;
    }

    public User UpdateFromIdp(string firstName, string? lastName, string? email, string username)
    {
        FirstName = firstName;
        LastName = lastName;
        Email = EmailOf(email);
        Username = username;

        ValidateUser(this);
        QueueDomainEvent(new UserUpdated(Id));

        return this;
    }

    public bool HasRole(UserRole role) => _roles.Any(x => x.Role == role);

    /// <summary>
    /// Adds the role, and ends each override of a permission that the role gives: a Deny, so the user holds each
    /// permission of the role after the add, and a Grant, which the role makes redundant. A second add of a role that
    /// the user has ends the same overrides, so it sets the user back to each permission of the role.
    /// </summary>
    public RoleChange AddRole(UserRole role)
    {
        var given = role.Permissions().ToHashSet();
        var cleared = _userPermissions.Where(x => given.Contains(x.Permission)).ToList();
        foreach (var row in cleared)
            _userPermissions.Remove(row);

        var added = HasRole(role) ? null : UserRoleAssignment.Create(this, role);
        if (added is not null)
            _roles.Add(added);

        var change = new RoleChange(added, null, cleared);
        if (change.Changed)
            QueueDomainEvent(new UserUpdated(Id));
        return change;
    }

    /// <summary>
    /// Removes the role. A permission that another role or an added permission gives stays. A Deny of a permission that
    /// no role left gives ends, because it removes nothing, so the overrides hold only what differs from the roles.
    /// </summary>
    public RoleChange RemoveRole(UserRole role)
    {
        var removed = _roles.FirstOrDefault(x => x.Role == role);
        if (removed is null)
            return RoleChange.None;

        _roles.Remove(removed);
        var given = FromRoles();
        var cleared = _userPermissions.Where(x => x.Effect == PermissionEffect.Deny && !given.Contains(x.Permission)).ToList();
        foreach (var row in cleared)
            _userPermissions.Remove(row);

        QueueDomainEvent(new UserUpdated(Id));
        return new RoleChange(null, removed, cleared);
    }

    /// <summary>
    /// Gives the user the permission. A removal of it ends, and an addition is kept only when no role gives it, so
    /// the overrides hold only what differs from the roles.
    /// </summary>
    public OverrideChange AddPermission(Permission permission)
        => SetOverride(permission, FromRoles().Contains(permission) ? null : PermissionEffect.Grant);

    /// <summary>
    /// Takes the permission away from the user. An addition of it ends, and a removal is kept only when a role gives it.
    /// </summary>
    public OverrideChange RemovePermission(Permission permission)
        => SetOverride(permission, FromRoles().Contains(permission) ? PermissionEffect.Deny : null);

    private OverrideChange SetOverride(Permission permission, PermissionEffect? effect)
    {
        var existing = _userPermissions.FirstOrDefault(x => x.Permission == permission);
        if (existing?.Effect == effect)
            return OverrideChange.None;

        if (existing is not null)
            _userPermissions.Remove(existing);
        var added = effect is { } e ? UserPermission.Create(this, permission, e) : null;
        if (added is not null)
            _userPermissions.Add(added);

        return new OverrideChange(added, existing);
    }

    /// <summary>The permissions that the user holds: <see cref="Roles"/> with <see cref="UserPermissions"/> on top.</summary>
    /// <remarks>Load <see cref="Roles"/> and <see cref="UserPermissions"/> first.</remarks>
    public IReadOnlySet<Permission> Permissions()
        => EffectivePermissions.Of(
            _roles.Select(x => x.Role),
            _userPermissions.Where(x => x.Effect == PermissionEffect.Grant).Select(x => x.Permission),
            _userPermissions.Where(x => x.Effect == PermissionEffect.Deny).Select(x => x.Permission));

    /// <summary>The permissions that the user would hold with no override.</summary>
    public IReadOnlySet<Permission> FromRoles() => EffectivePermissions.FromRoles(_roles.Select(x => x.Role));

    /// <summary>
    /// Call it in the feature that removes the user. A handler of <see cref="UserDeleted"/> in another domain can then
    /// clear or mark a link to the user, in the same save. See <c>docs/backend/domain-events.md</c>.
    /// The soft-deleted row stays, so the delete clears the name, the email, and the username of the person.
    /// </summary>
    public User Delete()
    {
        QueueDomainEvent(new UserDeleted(Id));
        DeleteQueued = true;
        FirstName = string.Empty;
        LastName = null;
        Email = null;
        Username = string.Empty;
        return this;
    }

    BaseEntity IDeletedWithEvent.Delete() => Delete();

    bool IDeletedWithEvent.DeleteQueued => DeleteQueued;

    public bool HasPermission(Permission permission) => Permissions().Contains(permission);

    /// <summary>
    /// The actions that the caller can send for this user now. The read DTO carries them, so the SPA shows a button
    /// only for an action that <see cref="GuardAllows"/> lets through.
    /// </summary>
    /// <remarks>Load <see cref="Roles"/> and <see cref="UserPermissions"/> first, because some actions read them.</remarks>
    public IReadOnlyList<UserAction> AllowedActions(Caller caller)
    {
        var permissions = CurrentPermissions();
        return UserAction.AllowedFor(OnThisRecord(caller, permissions), Id, permissions);
    }

    /// <summary>
    /// The roles that the user lacks and the caller can add now: the caller may send AddRole and holds each permission
    /// of the role, as the command requires.
    /// </summary>
    /// <remarks>Load <see cref="Roles"/> and <see cref="UserPermissions"/> first.</remarks>
    public IReadOnlyList<UserRole> AddableRoles(Caller caller)
    {
        var permissions = CurrentPermissions();
        return UserRole.AddableBy(OnThisRecord(caller, permissions), Id, permissions, _roles.Select(x => x.Role.Value));
    }

    /// <summary>The check that each command of <see cref="UserAction"/> makes after it loads the user.</summary>
    /// <exception cref="Exceptions.ForbiddenAccessException">The caller lacks a permission that the action needs.</exception>
    public void GuardAllows(UserAction action, Caller caller)
    {
        var permissions = CurrentPermissions();
        action.Guard(OnThisRecord(caller, permissions), Id, permissions);
    }

    private Permission[] CurrentPermissions() => [.. Permissions()];

    // A member holds the permissions of their own user. After a command changes them, the caller that the handler
    // loaded still has the old set, so the response would list the actions from before the change.
    private Caller OnThisRecord(Caller caller, Permission[] permissions)
        => caller.IsUser(Id) ? new Caller.Member(Id, permissions) : caller;

    private static EmailAddress? EmailOf(string? value) => value is null ? null : EmailAddress.Of(value);

    private static void ValidateUser(User user)
    {
        Exceptions.ValidationException.ThrowWhenNullOrWhitespace(user.TenantId, "tenantId", "Please provide a tenant.");
        Exceptions.ValidationException.Must(EntityId.IsValid(user.TenantId, Tenants.Tenant.IdPrefix), "tenantId", "Please provide a valid tenant ID.");
        Exceptions.ValidationException.ThrowWhenNullOrWhitespace(user.FirstName, "firstName", "Please provide a first name.");
        Exceptions.ValidationException.ThrowWhenLongerThan(user.FirstName, FirstNameMaxLength, "firstName",
            $"A first name can have no more than {FirstNameMaxLength} characters.");
        Exceptions.ValidationException.ThrowWhenHasCharacterNotStorable(user.FirstName, "firstName",
            "A first name has a character that is not allowed.");
        if (user.LastName is not null)
        {
            Exceptions.ValidationException.ThrowWhenNullOrWhitespace(user.LastName, "lastName", "Please provide a last name.");
            Exceptions.ValidationException.ThrowWhenLongerThan(user.LastName, LastNameMaxLength, "lastName",
                $"A last name can have no more than {LastNameMaxLength} characters.");
            Exceptions.ValidationException.ThrowWhenHasCharacterNotStorable(user.LastName, "lastName",
                "A last name has a character that is not allowed.");
        }
        Exceptions.ValidationException.ThrowWhenNullOrWhitespace(user.Identifier, "identifier", "Please provide an identifier.");
        Exceptions.ValidationException.ThrowWhenLongerThan(user.Identifier, IdentifierMaxLength, "identifier",
            $"An identifier can have no more than {IdentifierMaxLength} characters.");
        Exceptions.ValidationException.ThrowWhenHasCharacterNotStorable(user.Identifier, "identifier",
            "An identifier has a character that is not allowed.");
        Exceptions.ValidationException.ThrowWhenNullOrWhitespace(user.Username, "username", "Please provide a username.");
        Exceptions.ValidationException.ThrowWhenLongerThan(user.Username, UsernameMaxLength, "username",
            $"A username can have no more than {UsernameMaxLength} characters.");
        Exceptions.ValidationException.ThrowWhenHasCharacterNotStorable(user.Username, "username",
            "A username has a character that is not allowed.");
    }

    protected User() : base(IdPrefix) { } // For EF Core
}

/// <summary>
/// The rows that a change of an override adds and removes. The navigation uses field access, so the handler tells the
/// context about each row.
/// </summary>
public sealed record OverrideChange(UserPermission? Added, UserPermission? Removed)
{
    public static OverrideChange None { get; } = new(null, null);

    public bool Changed => Added is not null || Removed is not null;
}

/// <summary>The rows that a change of a role adds and removes, with each override that the change ends.</summary>
public sealed record RoleChange(UserRoleAssignment? Added, UserRoleAssignment? Removed, IReadOnlyList<UserPermission> ClearedOverrides)
{
    public static RoleChange None { get; } = new(null, null, []);

    public bool Changed => Added is not null || Removed is not null || ClearedOverrides.Count > 0;
}
