namespace Recall.Server.Domain.ImportBatches;

/// <summary>The counts of an accept (IMP-20). An open batch holds zeros.</summary>
public sealed record ImportAcceptResult(int SessionsMade, int EntriesRejected, int DaysRejected, int SkippedInJournal, int SkippedDeleted)
{
    public static readonly ImportAcceptResult None = new(0, 0, 0, 0, 0);
}
