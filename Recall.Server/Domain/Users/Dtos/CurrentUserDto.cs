namespace Recall.Server.Domain.Users.Dtos;

using Resources;

/// <summary>The signed-in caller as the Server sees it in the access token.</summary>
public sealed record CurrentUserDto
{
    /// <summary>The <c>sub</c> claim of the token.</summary>
    [RequiredField]
    public string Identifier { get; init; } = default!;

    /// <summary>The display name from the token, or null when the identity provider sends no name.</summary>
    public string? Name { get; init; }

    /// <summary>True when the caller is the Owner of this install, in person. A personal access token is never the Owner.</summary>
    public bool IsOwner { get; init; }
}
