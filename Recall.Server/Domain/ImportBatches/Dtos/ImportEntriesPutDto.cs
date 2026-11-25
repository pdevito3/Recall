namespace Recall.Server.Domain.ImportBatches.Dtos;

using Resources;
using Sessions;
using TimeZones;

public sealed record ImportEntriesPutDto
{
    /// <summary>At most 500 entries, each Client session ID once.</summary>
    [RequiredField]
    public IReadOnlyList<ImportEntryPutDto> Entries { get; init; } = [];
}

public sealed record ImportEntryPutDto
{
    /// <summary>The ID that the Session of the entry gets at the accept. The same ID again changes the same entry.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.ClientSessionIdMaxLength))]
    public string ClientSessionId { get; init; } = default!;

    /// <summary>The day of the source, as <c>YYYY-MM-DD</c>.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(ImportAgentLayer), nameof(ImportAgentLayer.DateTextLength))]
    public string Date { get; init; } = default!;

    /// <summary>The order of the entry in its day. With no time, the entry starts at 12:00 plus this number of minutes.</summary>
    [SchemaMinimum(typeof(ImportAgentLayer), nameof(ImportAgentLayer.PositionMin))]
    [SchemaMaximum(typeof(ImportAgentLayer), nameof(ImportAgentLayer.PositionMax))]
    public required int Position { get; init; }

    /// <summary>The time of day as <c>HH:mm</c>, only when the source gives one.</summary>
    [SchemaMaxLength(typeof(ImportTimeOfDay), nameof(ImportTimeOfDay.TextLength))]
    public string? Time { get; init; }

    /// <summary>The IANA zone of the entry, or null for the zone of the batch.</summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }

    /// <summary>The words of the source as Markdown, with no change to the words of the person.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(ImportAgentLayer), nameof(ImportAgentLayer.RawMaxLength))]
    public string Raw { get; init; } = default!;

    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string>? Moods { get; init; }

    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string>? Topics { get; init; }
}
