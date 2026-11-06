namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record DraftSaveResultDto
{
    /// <summary>A save with the outcome <c>Lost</c> did not change the Draft, and its words stay as the newest Revision.</summary>
    [EnumNames<DraftSaveOutcome>]
    public string Outcome { get; init; } = default!;
    public int DraftRevision { get; init; }
    public int LatestRevision { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public uint Version { get; init; }
}
