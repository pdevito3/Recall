namespace Recall.Server.Domain.ImportBatches.Dtos;

using Resources;
using TimeZones;

public sealed record ImportBatchForCreationDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(ImportBatch), nameof(ImportBatch.NameMaxLength))]
    public string Name { get; init; } = default!;

    /// <summary>The IANA zone of the source, such as <c>America/Chicago</c>. Each entry with no zone of its own takes it.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string TimeZone { get; init; } = default!;
}
