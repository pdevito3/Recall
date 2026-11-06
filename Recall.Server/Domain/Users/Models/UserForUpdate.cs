namespace Recall.Server.Domain.Users.Models;

public sealed record UserForUpdate
{
    public string FirstName { get; init; } = default!;
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string Username { get; init; } = default!;
    public uint Version { get; init; }
}
