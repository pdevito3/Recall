namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record DraftSaveResultDto
{
    /// <summary><c>Won</c> and <c>Unchanged</c> say that the Draft holds these words. <c>Kept</c> says that an earlier try of this save won, at the number in <c>draftRevision</c>, and that a chunk or a later save moved the Draft after it. <c>Lost</c> says that the Draft never took these words, and they are in a Revision.</summary>
    [EnumNames<DraftSaveOutcome>]
    public string Outcome { get; init; } = default!;
    /// <summary>The base of the next save: the number of a Draft whose words the client holds. After <c>Kept</c> it is the number at which this save won, which is below the current Draft. It is null after <c>Lost</c>, because the client holds the words of no Draft: read the Draft, merge, and save from the number of the read.</summary>
    public int? DraftRevision { get; init; }
    /// <summary>The number of the newest Revision. It is never a base.</summary>
    public int LatestRevision { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public uint Version { get; init; }
}
