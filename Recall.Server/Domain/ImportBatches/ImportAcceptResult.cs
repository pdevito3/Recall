namespace Recall.Server.Domain.ImportBatches;

using Sessions;

public sealed record ImportAcceptResult(int SessionsMade, int EntriesRejected, int DaysRejected, int SkippedInJournal, int SkippedDeleted)
{
    public static readonly ImportAcceptResult None = new(0, 0, 0, 0, 0);
}

public sealed record ImportAccept(IReadOnlyList<Session> Sessions, IReadOnlyList<ImportEntry> Removed);
