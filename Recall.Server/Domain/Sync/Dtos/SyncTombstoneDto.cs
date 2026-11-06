namespace Recall.Server.Domain.Sync.Dtos;

/// <summary>A row that is gone. A device removes its copy, and any local Session with this client session ID.</summary>
public sealed record SyncTombstoneDto
{
    [EnumNames<SyncEntityType>]
    public string EntityType { get; init; } = default!;
    public string Id { get; init; } = default!;
    public string? ClientSessionId { get; init; }
}
