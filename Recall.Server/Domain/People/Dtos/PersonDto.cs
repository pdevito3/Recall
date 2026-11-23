namespace Recall.Server.Domain.People.Dtos;

public sealed record PersonDto
{
    public string Id { get; init; } = default!;
    public string Label { get; init; } = default!;
    public string? FullName { get; init; }
    public string? Notes { get; init; }
    public uint Version { get; init; }
}
