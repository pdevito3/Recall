namespace Recall.Server.Domain.PersonalAccessTokens.Dtos;

using Auth.Entities;
using Resources;
using Users;

public sealed record PersonalAccessTokenForCreationDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(AuthSession), nameof(AuthSession.NameMaxLength))]
    public string Name { get; init; } = default!;

    /// <summary>The permissions of the grant. The caller must hold each one. No permission when it is not sent.</summary>
    [NamesOf<Permission>]
    public IReadOnlyList<string>? Permissions { get; init; }

    /// <summary>The token reads the journal of the person.</summary>
    public required bool ReadJournal { get; init; }

    /// <summary>The token changes the journal of the person. It needs readJournal.</summary>
    public required bool WriteJournal { get; init; }

    /// <summary>The end of the token, in the future. Send it or neverExpires, not both.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True for a token that works until a revoke. Send it or expiresAt, not both.</summary>
    public bool? NeverExpires { get; init; }
}
