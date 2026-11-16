namespace Recall.Server.Domain.PersonalAccessTokens.Dtos;

using Users;

/// <summary>A personal access token as a list shows it. It holds no value and no hash.</summary>
public sealed record PersonalAccessTokenDto
{
    public string Id { get; init; } = default!;
    public string UserId { get; init; } = default!;
    public string Name { get; init; } = default!;

    /// <summary>The permissions of the grant. The token holds only those that its person holds too.</summary>
    [NamesOf<Permission>]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    public bool ReadJournal { get; init; }
    public bool WriteJournal { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null for a token that never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Null for a token with no use yet. It can be up to an hour stale.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The actions that the caller can send for this token now. A button in the SPA reads this list.</summary>
    [NamesOf<PersonalAccessTokenAction>]
    public IReadOnlyList<string> AllowedActions { get; init; } = [];
}
