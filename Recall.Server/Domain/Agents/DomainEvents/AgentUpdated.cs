namespace Recall.Server.Domain.Agents.DomainEvents;

public sealed record AgentUpdated(string Id) : DomainEvent;
