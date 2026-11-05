namespace Recall.Server.Examples.Workshops.DomainEvents;

using Domain;

public sealed record WorkshopAttachmentAdded(string AttachmentId, string WorkshopId) : DomainEvent;
