namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record RevisionDto
{
    public int Number { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string Text { get; init; } = default!;

    /// <summary>The ID that the client gave these words. Null for a save with no ID and for a write of the Server.</summary>
    public string? ChangeId { get; init; }

    /// <summary>The ID of the change that these words grew from, as the client sent it.</summary>
    public string? ParentChangeId { get; init; }
}
