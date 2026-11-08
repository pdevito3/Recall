namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record RevisionSummaryDto
{
    public int Number { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>The number of characters of the Revision.</summary>
    public int Length { get; init; }

    /// <summary>True when the Draft took these words at this number. False for a save that lost.</summary>
    public bool WasDraft { get; init; }

    /// <summary>The ID that the client gave these words. Null for a save with no ID and for a write of the Server.</summary>
    public string? ChangeId { get; init; }

    /// <summary>The ID of the change that these words grew from, as the client sent it.</summary>
    public string? ParentChangeId { get; init; }
}
