namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record JournalDayDto
{
    /// <summary>The Journaling day: the day of the start of a Session in its own zone.</summary>
    public DateOnly Date { get; init; }

    public int SessionCount { get; init; }
}
