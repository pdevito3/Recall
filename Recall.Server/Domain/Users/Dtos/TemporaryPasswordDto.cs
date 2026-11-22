namespace Recall.Server.Domain.Users.Dtos;

/// <summary>The one answer that holds a temporary password. The Server keeps only its hash, so no later read gives it.</summary>
public sealed record TemporaryPasswordDto
{
    public string LoginName { get; init; } = default!;

    /// <summary>The password to give the person. It signs in only to the page where the person picks a password.</summary>
    public string Password { get; init; } = default!;

    public DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => "[temporary password]";
}
