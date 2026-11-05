namespace Recall.Server.Domain.Agents.DomainEvents;

public sealed record AgentCreated(string AgentId) : DomainEvent;
