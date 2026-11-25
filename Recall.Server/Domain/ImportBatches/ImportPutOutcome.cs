namespace Recall.Server.Domain.ImportBatches;

/// <summary>The outcome of one entry of a put, in the order of IMP-28: an entry gets the first that applies.</summary>
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
    /// <summary>The outcome of an entry with a valid agent layer, from the facts of IMP-28 in its order.</summary>
    /// <param name="identity">Where the ID is now (IMP-4).</param>
    /// <param name="inTheFuture">The start of the entry after the put is after now (IMP-9).</param>
    /// <param name="positionTaken">Another entry holds the date and position after the call (IMP-8).</param>
    /// <param name="rejected">The entry after the put is rejected, by its own review or by its day (IMP-6).</param>
    /// <param name="unchanged">The batch holds the entry with this agent layer already.</param>
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

    /// <summary>True for an outcome that stores the agent layer (IMP-28).</summary>
    public static bool Stores(this ImportPutOutcome outcome) => outcome is ImportPutOutcome.Rejected or ImportPutOutcome.Changed or ImportPutOutcome.Added;
}
