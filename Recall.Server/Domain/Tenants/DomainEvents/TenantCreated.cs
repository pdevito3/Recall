namespace Recall.Server.Domain.Tenants.DomainEvents;

public sealed record TenantCreated(string TenantId) : DomainEvent;
