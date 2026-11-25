namespace Recall.Server.Domain.ImportBatches;

/// <summary>The outcome of one ID of a remove (IMP-13).</summary>
public enum ImportRemoveOutcome
{
    Removed,
    NotFound,
    Rejected,
}

public sealed record ImportRemoveResult(string ClientSessionId, ImportRemoveOutcome Outcome);

/// <summary>The outcomes of a remove, in the order of the call, and the entries that the caller deletes.</summary>
public sealed record ImportRemove(IReadOnlyList<ImportRemoveResult> Results, IReadOnlyList<ImportEntry> Removed);
