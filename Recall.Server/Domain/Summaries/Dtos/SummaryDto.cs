namespace Recall.Server.Domain.Summaries.Dtos;

public sealed record SummaryDto
{
    /// <summary>Null while the period has no Summary.</summary>
    public string? Id { get; init; }

    [EnumNames<SummaryPeriod>]
    public string Period { get; init; } = default!;

    /// <summary>The first Journaling day of the period. A Week starts on a Monday.</summary>
    public DateOnly PeriodDate { get; init; }

    /// <summary>The last Journaling day of the period.</summary>
    public DateOnly PeriodLastDate { get; init; }

    /// <summary><c>Missing</c> and <c>Stale</c> come from the sources now. <c>Failed</c> is a run that gave no Summary.</summary>
    [EnumNames<SummaryStatus>]
    public string Status { get; init; } = default!;

    /// <summary>Why the last run failed. It is a closed code, and the words of a model provider never come here.</summary>
    [EnumNames<SummaryFailure>]
    public string Failure { get; init; } = nameof(SummaryFailure.None);

    /// <summary>The words of the Summary when the status is <c>Ready</c> or <c>Stale</c>, and empty otherwise.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>The sources of the period now: Sessions for a Day or a Week, and source periods with Sessions for a roll-up.</summary>
    public int SourceCount { get; init; }

    public DateTimeOffset? GeneratedAt { get; init; }
}
