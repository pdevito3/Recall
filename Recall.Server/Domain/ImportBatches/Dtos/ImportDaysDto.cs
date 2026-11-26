namespace Recall.Server.Domain.ImportBatches.Dtos;

public sealed record ImportDaysDto
{
    /// <summary>The version of the batch when the days were read. Send it with the accept.</summary>
    public uint Version { get; init; }

    /// <summary>Each date that has an entry or a reject of its day, in order.</summary>
    public IReadOnlyList<ImportDayDto> Days { get; init; } = [];
}

public sealed record ImportDayDto
{
    public DateOnly Date { get; init; }

    /// <summary>The count of the entries of the date.</summary>
    public int EntryCount { get; init; }

    /// <summary>The count of the entries of the date that the person rejected one by one. The reject of the day does not count here.</summary>
    public int RejectedCount { get; init; }

    /// <summary>True when the person rejected the day, so that the accept makes no Session of an entry of the date.</summary>
    public bool Rejected { get; init; }
}
