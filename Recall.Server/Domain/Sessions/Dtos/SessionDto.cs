namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record SessionDto
{
    public string Id { get; init; } = default!;
    public string ClientSessionId { get; init; } = default!;
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The date of <see cref="StartedAt"/> in the time zone of the User. It changes when the time zone changes.</summary>
    public DateOnly JournalingDay { get; init; }

    public string RawText { get; init; } = default!;
    public int RawDraftRevision { get; init; }
    public int RawLatestRevision { get; init; }
    public DateTimeOffset? RawSavedAt { get; init; }
    public string CleanedText { get; init; } = default!;
    public int CleanedDraftRevision { get; init; }
    public int CleanedLatestRevision { get; init; }
    public DateTimeOffset? CleanedSavedAt { get; init; }
    public bool CleanedHasHandEdits { get; init; }
    public string? Synopsis { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public int? LastCleanupRawRevision { get; init; }
    public string Activity { get; init; } = default!;
    public IReadOnlyList<string> Moods { get; init; } = [];
    public IReadOnlyList<SessionTopicDto> Topics { get; init; } = [];
    public IReadOnlyList<string> PersonIds { get; init; } = [];
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public uint Version { get; init; }

    /// <summary>Send it with the next metadata save. A save of a Draft does not change it.</summary>
    public uint MetadataVersion { get; init; }
}
