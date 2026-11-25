namespace Recall.Server.Domain.ImportBatches;

public enum ImportPutOutcome
{
    Invalid,
    InJournal,
    Deleted,
    InOtherBatch,
    InTheFuture,
    PositionTaken,
    Rejected,
    Unchanged,
    Changed,
    Added,
}

public static class ImportPutOutcomes
{
    public static ImportPutOutcome Of(ImportIdentity identity, bool inTheFuture, bool positionTaken, bool rejected, bool unchanged) => true switch
    {
        _ when identity == ImportIdentity.InJournal => ImportPutOutcome.InJournal,
        _ when identity == ImportIdentity.Deleted => ImportPutOutcome.Deleted,
        _ when identity == ImportIdentity.InOtherBatch => ImportPutOutcome.InOtherBatch,
        _ when inTheFuture => ImportPutOutcome.InTheFuture,
        _ when positionTaken => ImportPutOutcome.PositionTaken,
        _ when rejected => ImportPutOutcome.Rejected,
        _ when identity == ImportIdentity.InThisBatch && unchanged => ImportPutOutcome.Unchanged,
        _ when identity == ImportIdentity.InThisBatch => ImportPutOutcome.Changed,
        _ => ImportPutOutcome.Added,
    };

    public static bool Stores(this ImportPutOutcome outcome) => outcome is ImportPutOutcome.Rejected or ImportPutOutcome.Changed or ImportPutOutcome.Added;
}
