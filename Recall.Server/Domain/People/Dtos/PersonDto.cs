namespace Recall.Server.Domain.People.Dtos;

public sealed record PersonDto
{
    public string Id { get; init; } = default!;
    public string Label { get; init; } = default!;
    public uint Version { get; init; }
}
