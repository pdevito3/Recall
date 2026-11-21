namespace Recall.Server.Domain.Users;

using AuditEntries;

/// <summary>A caller issued a temporary password to the user (PWD-14). The entry holds no form of the password.</summary>
[AuditActionName("temporary_password_issued")]
public sealed record TemporaryPasswordIssued(
    AuditSubject Subject,
    [property: Sensitive] string LoginName,
    [property: NotSensitive] DateTimeOffset ExpiresAt) : AuditAction(Subject);
