namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record SessionDto
{
    public string Id { get; init; } = default!;
    public string ClientSessionId { get; init; } = default!;
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The date of <see cref="StartedAt"/> in the zone that the create stored. A later change of the settings does not move it.</summary>
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

    /// <summary>The number of the Cleaned Draft at the start of the last run. While the run runs, a newer Cleaned means its result will wait in History.</summary>
    public int? CleanupBaseCleanedRevision { get; init; }

    /// <summary>The Cleaned Revision that holds a Cleanup result that did not replace Cleaned, because Cleaned moved during its run. Null when none waits.</summary>
    public int? WaitingCleanupRevision { get; init; }

    /// <summary>Why the last Cleanup failed. It is a closed code, and the words of a model provider never come here.</summary>
    [EnumNames<CleanupFailure>]
    public string CleanupFailure { get; init; } = nameof(Sessions.CleanupFailure.None);
    public IReadOnlyList<SessionSuggestionDto> Suggestions { get; init; } = [];
    public IReadOnlyList<PersonProposalDto> PeopleProposals { get; init; } = [];
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
