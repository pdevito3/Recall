namespace Recall.Server.Domain.ImportBatches;

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
    public static ImportIdentity Classify(bool isSession, bool hasTombstone, string? batchOfEntry, string batchId) => true switch
    {
        _ when isSession => ImportIdentity.InJournal,
        _ when hasTombstone => ImportIdentity.Deleted,
        _ when batchOfEntry is null => ImportIdentity.New,
        _ when batchOfEntry == batchId => ImportIdentity.InThisBatch,
        _ => ImportIdentity.InOtherBatch,
    };
}
