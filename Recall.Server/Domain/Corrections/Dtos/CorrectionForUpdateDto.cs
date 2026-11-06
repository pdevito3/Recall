namespace Recall.Server.Domain.Corrections.Dtos;

using Resources;

public sealed record CorrectionForUpdateDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Correction), nameof(Correction.CanonicalTermMaxLength))]
    public string CanonicalTerm { get; init; } = default!;
    [SchemaMaxLength(typeof(Correction), nameof(Correction.MishearingMaxLength))]
    public IReadOnlyList<string> Mishearings { get; init; } = [];
    public bool HardReplace { get; init; }
    public required uint Version { get; init; }
}
