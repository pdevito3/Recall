namespace Recall.Server.Domain.ImportBatches;

public sealed record ImportAcceptResult(int SessionsMade, int EntriesRejected, int DaysRejected, int SkippedInJournal, int SkippedDeleted)
{
    public static readonly ImportAcceptResult None = new(0, 0, 0, 0, 0);
}
