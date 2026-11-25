namespace Recall.Server.Domain.ImportBatches;

/// <summary>Where a Client session ID is now, in the one space of IDs of a User (IMP-4).</summary>
public enum ImportIdentity
{
    InJournal,
    Deleted,
    InOtherBatch,
    InThisBatch,
    New,
}

public static class ImportIdentities
{
    /// <param name="isSession">A Session of the User has the ID.</param>
    /// <param name="hasTombstone">The Sync feed of the User has a Tombstone of the ID.</param>
    /// <param name="batchOfEntry">The batch of the entry with the ID, or null. Only an open batch has entries.</param>
    /// <param name="batchId">The batch of the call.</param>
    public static ImportIdentity Classify(bool isSession, bool hasTombstone, string? batchOfEntry, string batchId) => true switch
    {
        _ when isSession => ImportIdentity.InJournal,
        _ when hasTombstone => ImportIdentity.Deleted,
        _ when batchOfEntry is null => ImportIdentity.New,
        _ when batchOfEntry == batchId => ImportIdentity.InThisBatch,
        _ => ImportIdentity.InOtherBatch,
    };
}
