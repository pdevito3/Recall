namespace Recall.Server.Domain.ImportBatches.Dtos;

public sealed record ImportEntryPageDto
{
    /// <summary>The version of the batch when the entries were read. Send it with the accept.</summary>
    public uint Version { get; init; }

    /// <summary>The entries in order of their date and position.</summary>
    public IReadOnlyList<ImportEntryDto> Entries { get; init; } = [];

    /// <summary>Each day that the person rejected. A rejected day holds each entry of its date.</summary>
    public IReadOnlyList<DateOnly> RejectedDays { get; init; } = [];
}

/// <summary>The entries of a batch as an agent reads them, with no document.</summary>
public sealed record ImportEntryPageForAgentDto
{
    /// <summary>The version of the batch when the entries were read. Send it with the accept.</summary>
    public uint Version { get; init; }

    /// <summary>The entries in order of their date and position.</summary>
    public IReadOnlyList<ImportEntryForAgentDto> Entries { get; init; } = [];

    /// <summary>Each day that the person rejected. A rejected day holds each entry of its date.</summary>
    public IReadOnlyList<DateOnly> RejectedDays { get; init; } = [];
}

/// <summary>An entry as an agent reads it. It has no document, because the agent sent Raw and reads Raw.</summary>
public record ImportEntryForAgentDto
{
    public string ClientSessionId { get; init; } = default!;
    public DateOnly Date { get; init; }
    public int Position { get; init; }

    /// <summary>The time of the agent as <c>HH:mm</c>, or null.</summary>
    public string? Time { get; init; }

    /// <summary>The zone of the agent, or null for the zone of the batch.</summary>
    public string? TimeZone { get; init; }

    /// <summary>The Markdown of the agent, as the put sent it.</summary>
    public string Raw { get; init; } = default!;
    public IReadOnlyList<string> Moods { get; init; } = [];
    public IReadOnlyList<string> Topics { get; init; } = [];
    public ImportReviewDto Review { get; init; } = default!;

    /// <summary>True when the entry or its day is rejected. The accept makes no Session of it.</summary>
    public bool IsRejected { get; init; }

    /// <summary>The start of the Session that the accept makes now: the time of the person, else of the agent, else 12:00 plus the position in minutes.</summary>
    public DateTimeOffset StartsAt { get; init; }

    /// <summary>True when a put changed Raw after the last edit of the words by the person.</summary>
    public bool WordsChangedAfterEdit { get; init; }

    /// <summary>True when a put changed the time after the last edit of the time by the person.</summary>
    public bool TimeChangedAfterEdit { get; init; }
}

public sealed record ImportEntryDto : ImportEntryForAgentDto
{
    public ImportEntryDto() { }

    internal ImportEntryDto(ImportEntryForAgentDto entry, string document) : base(entry) => Document = document;

    /// <summary>The words that the accept stores now, as a ProseMirror document: the edit of the person, else Raw as a document.</summary>
    public string Document { get; init; } = default!;
}

public sealed record ImportReviewDto
{
    public bool Rejected { get; init; }

    /// <summary>The words of the person as a ProseMirror document, or null for the Raw of the agent.</summary>
    public string? Words { get; init; }

    /// <summary>The time of the person as <c>HH:mm</c>, or null for the time of the agent.</summary>
    public string? Time { get; init; }
}
