namespace Recall.Server.Domain.ImportBatches.Dtos;

using Resources;
using Sessions;

/// <summary>The whole review of one entry. Null clears an edit.</summary>
public sealed record ImportEntryReviewDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.ClientSessionIdMaxLength))]
    public string ClientSessionId { get; init; } = default!;

    public required bool Rejected { get; init; }

    /// <summary>The words of the person as a ProseMirror document, or null to keep the Raw of the agent.</summary>
    [SchemaMaxLength(typeof(Session), nameof(Session.DraftMaxStoredLength))]
    public string? Words { get; init; }

    /// <summary>The time of the person as <c>HH:mm</c>, or null to keep the time of the agent.</summary>
    [SchemaMaxLength(typeof(ImportTimeOfDay), nameof(ImportTimeOfDay.TextLength))]
    public string? Time { get; init; }
}

public sealed record ImportEntryReviewedDto
{
    /// <summary>The version of the batch after the review.</summary>
    public uint Version { get; init; }

    public ImportEntryDto Entry { get; init; } = default!;
}

public sealed record ImportDayReviewDto
{
    /// <summary>The date of the day as <c>YYYY-MM-DD</c>. It is in the body, so that no request span holds it.</summary>
    public required DateOnly Date { get; init; }

    public required bool Rejected { get; init; }
}

public sealed record ImportDayReviewedDto
{
    /// <summary>The version of the batch after the review.</summary>
    public uint Version { get; init; }

    public DateOnly Date { get; init; }
    public bool Rejected { get; init; }
}
