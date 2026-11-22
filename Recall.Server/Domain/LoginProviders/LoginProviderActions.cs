namespace Recall.Server.Domain.LoginProviders;

using AuditEntries;

// A provider is install-wide, so each action is recorded on the tenant of the Admin who made it. No action holds the client secret (IDP-18).

[AuditActionName("login_provider_added")]
public sealed record LoginProviderAdded(
    AuditSubject Subject,
    [property: NotSensitive] string ProviderId,
    [property: NotSensitive] string DisplayName,
    [property: NotSensitive] string Issuer,
    [property: NotSensitive] string ClientId,
    bool AllowHttp,
    [property: NotSensitive] string? CaCertificatePath) : AuditAction(Subject);

[AuditActionName("login_provider_updated")]
public sealed record LoginProviderUpdated(
    AuditSubject Subject,
    [property: NotSensitive] string ProviderId,
    [property: NotSensitive] string DisplayName,
    [property: NotSensitive] string ClientId,
    bool AllowHttp,
    [property: NotSensitive] string? CaCertificatePath,
    bool SecretChanged) : AuditAction(Subject);

[AuditActionName("login_provider_deleted")]
public sealed record LoginProviderDeleted(
    AuditSubject Subject,
    [property: NotSensitive] string ProviderId,
    [property: NotSensitive] string Issuer) : AuditAction(Subject);
