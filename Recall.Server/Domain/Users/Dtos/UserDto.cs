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
    [NamesOf<UserRole>]
    public string Role { get; init; } = default!;
    [NamesOf<Permission>]
    public IReadOnlyList<string> Permissions { get; init; } = [];
    /// <summary>The actions that the caller can send for this user now. A button in the SPA reads this list.</summary>
    [NamesOf<UserAction>]
    public IReadOnlyList<string> AllowedActions { get; init; } = [];
    public uint Version { get; init; }
}
