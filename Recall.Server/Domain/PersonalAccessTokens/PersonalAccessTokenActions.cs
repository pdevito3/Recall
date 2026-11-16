namespace Recall.Server.Domain.PersonalAccessTokens;

using AuditEntries;

/// <summary>
/// A person made a personal access token. The entry maps the <c>pat_…</c> client of later entries to a name, also after
/// the sweep removes the row. It holds no form of the value.
/// </summary>
[AuditActionName("access_token_created")]
public sealed record AccessTokenCreated(
    AuditSubject Subject,
    [property: NotSensitive] string TokenId,
    [property: Sensitive] string Name,
    [property: NotSensitive] string Grant,
    [property: NotSensitive] DateTimeOffset? ExpiresAt) : AuditAction(Subject);

/// <summary>A person revoked a personal access token from the API. A revoke by revoke-others or a user delete records none.</summary>
[AuditActionName("access_token_revoked")]
public sealed record AccessTokenRevoked(
    AuditSubject Subject,
    [property: NotSensitive] string TokenId,
    [property: Sensitive] string Name,
    [property: NotSensitive] string Grant,
    [property: NotSensitive] DateTimeOffset? ExpiresAt) : AuditAction(Subject);
