namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record RevisionSummaryDto
{
    public int Number { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>The number of characters of the Revision.</summary>
    public int Length { get; init; }
}
