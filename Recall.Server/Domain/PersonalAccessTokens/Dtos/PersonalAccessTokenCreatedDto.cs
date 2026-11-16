namespace Recall.Server.Domain.PersonalAccessTokens.Dtos;

/// <summary>The one answer that holds the value of a token. The Server keeps only its hash, so no later read gives it.</summary>
public sealed record PersonalAccessTokenCreatedDto
{
    public PersonalAccessTokenDto Token { get; init; } = default!;

    /// <summary>The value to send as <c>Authorization: Bearer</c>. Copy it now.</summary>
    public string Value { get; init; } = default!;

    public override string ToString() => $"[personal access token {Token.Id}]";
}
