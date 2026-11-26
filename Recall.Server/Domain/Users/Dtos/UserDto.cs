namespace Recall.Server.Domain.Users.Dtos;

public sealed record UserDto
{
    public string Id { get; init; } = default!;
    public string TenantId { get; init; } = default!;
    public string FirstName { get; init; } = default!;
    public string? LastName { get; init; }
    public string FullName { get; init; } = default!;
    public string Identifier { get; init; } = default!;
    public string? Email { get; init; }
    public string Username { get; init; } = default!;
    /// <summary>The name that the user types to sign in with a password. Null until a first temporary password sets it.</summary>
    public string? LoginName { get; init; }
    /// <summary>The roles of the user. The permissions of each role add up. No role means a Member.</summary>
    [NamesOf<UserRole>]
    public IReadOnlyList<string> Roles { get; init; } = [];
    /// <summary>The permissions that the user holds: those of the roles, with the added ones, less the removed ones.</summary>
    [NamesOf<Permission>]
    public IReadOnlyList<string> Permissions { get; init; } = [];
    /// <summary>The permissions that this user holds and no role of the user gives.</summary>
    [NamesOf<Permission>]
    public IReadOnlyList<string> AddedPermissions { get; init; } = [];
    /// <summary>The permissions that a role of the user gives and this user does not hold.</summary>
    [NamesOf<Permission>]
    public IReadOnlyList<string> RemovedPermissions { get; init; } = [];
    /// <summary>The actions that the caller can send for this user now. A button in the SPA reads this list.</summary>
    [NamesOf<UserAction>]
    public IReadOnlyList<string> AllowedActions { get; init; } = [];
    /// <summary>The roles that the user does not have and the caller can add now. Empty when the caller cannot add a role.</summary>
    [NamesOf<UserRole>]
    public IReadOnlyList<string> AddableRoles { get; init; } = [];
    public uint Version { get; init; }
}
