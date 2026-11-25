namespace Recall.Server.Domain.ImportBatches.Dtos;

public sealed record ImportPutResultDto
{
    /// <summary>The version of the batch after the put. A put that stores nothing does not move it.</summary>
    public uint Version { get; init; }

    /// <summary>The outcome of each entry, in the order of the call.</summary>
    public IReadOnlyList<ImportPutOutcomeDto> Outcomes { get; init; } = [];
}

public sealed record ImportPutOutcomeDto
{
    public string ClientSessionId { get; init; } = default!;

    [EnumNames<ImportPutOutcome>]
    public string Outcome { get; init; } = default!;

    /// <summary>For <c>Invalid</c>, the field of the entry that is not valid.</summary>
    public string? Field { get; init; }

    /// <summary>For <c>Invalid</c>, why the field is not valid.</summary>
    public string? Reason { get; init; }

    /// <summary>For <c>PositionTaken</c>, the Client session ID of the entry that holds the date and the position.</summary>
    public string? PositionTakenBy { get; init; }
}
