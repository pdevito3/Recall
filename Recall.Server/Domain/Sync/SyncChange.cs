namespace Recall.Server.Domain.Sync;

[NotAudited("Triggers write it, and it holds only IDs.")]
public class SyncChange : IJournalOwned
{
    public long Sequence { get; private set; }

    public string UserId { get; private set; } = default!;

    public SyncEntityType EntityType { get; private set; }

    public string EntityId { get; private set; } = default!;

    public string? ClientSessionId { get; private set; }

    public bool IsTombstone { get; private set; }

    private SyncChange()
    {
    }
}
