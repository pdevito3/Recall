namespace Recall.Server.Domain.Sessions.Dtos;

/// <summary>One event of <c>POST /sessions/{id}/cleanup/stream</c>. An event holds no words of the journal and no words of a model provider.</summary>
public sealed record CleanupEventDto
{
    /// <summary><c>run.started</c>, <c>progress</c>, <c>completed</c>, <c>failed</c>, or <c>stopped</c>.</summary>
    public required string Type { get; init; }

    public CleanupEventDataDto Data { get; init; } = new();
}

public sealed record CleanupEventDataDto
{
    public string? Message { get; init; }

    /// <summary>The code of <see cref="CleanupFailure"/> of a <c>failed</c> event.</summary>
    [EnumNames<CleanupFailure>]
    public string StopReason { get; init; } = nameof(CleanupFailure.None);
}
