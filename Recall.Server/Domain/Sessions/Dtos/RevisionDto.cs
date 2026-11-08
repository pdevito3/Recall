namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record RevisionDto
{
    public int Number { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string Text { get; init; } = default!;
}
