namespace Recall.Server.Domain.Users.Dtos;

using Resources;

public sealed record TemporaryPasswordForIssueDto
{
    /// <summary>The name that the person types to sign in. The first issue needs it, and a later issue keeps the name that the first set.</summary>
    [SchemaMaxLength(typeof(Auth.LoginName), nameof(Auth.LoginName.MaxStoredLength))]
    public string? LoginName { get; init; }
}
