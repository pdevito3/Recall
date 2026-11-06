namespace Recall.Server.Domain.Corrections.Dtos;

public sealed record CorrectionDto
{
    public string Id { get; init; } = default!;
    public string CanonicalTerm { get; init; } = default!;
    public IReadOnlyList<string> Mishearings { get; init; } = [];
    public bool HardReplace { get; init; }
    public uint Version { get; init; }
}
