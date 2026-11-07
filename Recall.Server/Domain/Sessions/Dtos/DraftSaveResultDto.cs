namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record DraftSaveResultDto
{
    /// <summary><c>Won</c> and <c>Unchanged</c> say that the Draft holds these words. <c>Kept</c> says that the Draft took these words at the number in <c>draftRevision</c>, and that a chunk or a later save moved the Draft after it. <c>Lost</c> says that the Draft did not take this save, and its words are in a Revision.</summary>
    [EnumNames<DraftSaveOutcome>]
    public string Outcome { get; init; } = default!;
    /// <summary>After <c>Won</c> or <c>Unchanged</c>, the base of the next save. After <c>Kept</c>, the number at which the Draft took these words, which is older than the Draft and is not a base: read the Session before the next save. After <c>Lost</c>, null: read the Session, merge, and save from the number of the read, with a new <c>savedAt</c>.</summary>
    public int? DraftRevision { get; init; }
    /// <summary>The number of the newest Revision. It is never a base.</summary>
    public int LatestRevision { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public uint Version { get; init; }
}
