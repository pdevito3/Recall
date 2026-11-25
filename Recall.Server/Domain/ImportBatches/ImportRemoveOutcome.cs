namespace Recall.Server.Domain.ImportBatches;

public enum ImportRemoveOutcome
{
    Removed,
    NotFound,
    Rejected,
}

public sealed record ImportRemoveResult(string ClientSessionId, ImportRemoveOutcome Outcome);

public sealed record ImportRemove(IReadOnlyList<ImportRemoveResult> Results, IReadOnlyList<ImportEntry> Removed);
