namespace Recall.Server.Domain.Users;

using AuditEntries;

// A provider can make the subject an email address, so each action holds it as sensitive, as User.Identifier is (IDP-18).

/// <summary>A person linked an identity at a provider to their own user from their own session (IDP-11).</summary>
[AuditActionName("external_login_linked")]
public sealed record ExternalLoginLinked(
    AuditSubject Subject,
    [property: NotSensitive] string Issuer,
    [property: Sensitive] string IdentitySubject,
    [property: Sensitive] string? DisplayLabel) : AuditAction(Subject);

/// <summary>A person, or a caller with manage_user_access, removed a link of the user (IDP-12).</summary>
[AuditActionName("external_login_removed")]
public sealed record ExternalLoginRemoved(
    AuditSubject Subject,
    [property: NotSensitive] string Issuer,
    [property: Sensitive] string IdentitySubject,
    [property: Sensitive] string? DisplayLabel) : AuditAction(Subject);
