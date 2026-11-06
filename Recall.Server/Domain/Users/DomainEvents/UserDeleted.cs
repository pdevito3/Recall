namespace Recall.Server.Domain.Users.DomainEvents;

public sealed record UserDeleted(string UserId) : DomainEvent;
