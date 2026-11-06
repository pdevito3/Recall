namespace Recall.Server.Domain.Sessions.Dtos;

/// <summary>One row of the timeline. It has no Draft, so a page costs the same whatever the length of the text.</summary>
public sealed record SessionSummaryDto
{
    public string Id { get; init; } = default!;
    public string ClientSessionId { get; init; } = default!;
    public DateTimeOffset StartedAt { get; init; }
    public DateOnly JournalingDay { get; init; }
    public string Preview { get; init; } = default!;
    public string? Synopsis { get; init; }
    [EnumNames<CleanupStatus>]
    public string CleanupStatus { get; init; } = default!;
    public string Activity { get; init; } = default!;
    public IReadOnlyList<string> Moods { get; init; } = [];
    public IReadOnlyList<SessionTopicDto> Topics { get; init; } = [];
    public IReadOnlyList<string> PersonIds { get; init; } = [];
    public uint Version { get; init; }
}
