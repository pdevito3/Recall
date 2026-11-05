namespace Recall.Server.Examples.Venues.DomainEvents;

using Domain;

public sealed record VenueCreated(string VenueId) : DomainEvent;
