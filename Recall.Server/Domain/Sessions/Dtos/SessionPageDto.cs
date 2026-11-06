namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record SessionPageDto
{
    /// <summary>The Sessions of the page, newest first.</summary>
    public IReadOnlyList<SessionSummaryDto> Items { get; init; } = [];

    /// <summary>Send it as the cursor of the next request to get the next page. Null on the last page.</summary>
    public string? NextCursor { get; init; }
}
