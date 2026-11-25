namespace Recall.Server.Domain.ImportBatches.Dtos;

public sealed record ImportBatchDto
{
    public string Id { get; init; } = default!;
    public string Name { get; init; } = default!;

    /// <summary>The IANA zone of each entry that has no zone of its own.</summary>
    public string TimeZone { get; init; } = default!;

    [EnumNames<ImportBatchState>]
    public string State { get; init; } = default!;
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }

    /// <summary>The entries that the batch holds now. The accept removes them, so an accepted batch has none.</summary>
    public int EntryCount { get; init; }

    /// <summary>The counts of the accept, or null while the batch is open.</summary>
    public ImportAcceptResultDto? Result { get; init; }

    /// <summary>Send it with the accept. Each write to the batch, its entries, or its days moves it.</summary>
    public uint Version { get; init; }
}

public sealed record ImportAcceptResultDto
{
    public int SessionsMade { get; init; }
    public int EntriesRejected { get; init; }
    public int DaysRejected { get; init; }

    /// <summary>The kept entries whose ID became a Session after the put. The Session stays as it is.</summary>
    public int SkippedInJournal { get; init; }

    /// <summary>The kept entries whose ID has a Tombstone, because the person deleted its Session after the put.</summary>
    public int SkippedDeleted { get; init; }
}
