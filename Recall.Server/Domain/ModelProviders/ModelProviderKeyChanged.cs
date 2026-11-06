namespace Recall.Server.Domain.ModelProviders;

using AuditEntries;

/// <summary>A person saved a new API key. The entry says so, and it holds no form of the key.</summary>
[AuditActionName("model_provider_key_changed")]
public sealed record ModelProviderKeyChanged(AuditSubject Subject) : AuditAction(Subject);
