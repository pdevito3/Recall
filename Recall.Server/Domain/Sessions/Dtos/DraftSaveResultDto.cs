namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record DraftSaveResultDto
{
    /// <summary><c>Unchanged</c> says that the Draft holds these words. <c>Lost</c> says that it does not, and the words are in a Revision, also on a replay.</summary>
    [EnumNames<DraftSaveOutcome>]
    public string Outcome { get; init; } = default!;
    /// <summary>The base of the next save. It is null after <c>Lost</c>, because the client does not hold the words of the Draft: read the Draft, merge, and save from the number of the read.</summary>
    public int? DraftRevision { get; init; }
    /// <summary>The number of the newest Revision. It is never a base.</summary>
    public int LatestRevision { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public uint Version { get; init; }
}
