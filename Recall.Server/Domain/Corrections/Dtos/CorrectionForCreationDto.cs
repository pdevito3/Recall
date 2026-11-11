namespace Recall.Server.Domain.Corrections.Dtos;

using Resources;

public sealed record CorrectionForCreationDto
{
    /// <summary>The ID that the device made for this Correction, or null for a new ID. A second create with this ID returns the same Correction.</summary>
    [SchemaMaxLength(typeof(EntityId), nameof(EntityId.MaxLength))]
    public string? Id { get; init; }
    [RequiredField]
    [SchemaMaxLength(typeof(Correction), nameof(Correction.CanonicalTermMaxLength))]
    public string CanonicalTerm { get; init; } = default!;
    [SchemaMaxLength(typeof(Correction), nameof(Correction.MishearingMaxLength))]
    public IReadOnlyList<string> Mishearings { get; init; } = [];
    public bool HardReplace { get; init; }
}
