namespace Recall.Server.Domain.Summaries;

using Databases;
using Microsoft.EntityFrameworkCore;
using TimeZones;
using Sessions;

public static class SummaryQueries
{
    // Each zone is within 14 hours of UTC, so a day in any zone starts and ends within a day of the same UTC day.
    private static readonly TimeSpan AnyZone = TimeSpan.FromDays(1);

    /// <summary>
    /// The Sessions with words whose Journaling day is in the days from <paramref name="first"/> up to <paramref name="end"/>.
    /// A Session with no words is no source. It reads no words, so a year of Sessions is one small read.
    /// </summary>
    public static async Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, DateOnly first, DateOnly end, CancellationToken cancellationToken, string? exceptSessionId = null)
    {
        var from = new ZonedDate(first, TimeZoneId.Utc).Start - AnyZone;
        var to = new ZonedDate(end, TimeZoneId.Utc).Start + AnyZone;
        var rows = await dbContext.Sessions
            .TagWithOperationCallSite("summaries.sources")
            .AsNoTracking()
            .Where(s => s.StartedAt >= from && s.StartedAt < to && s.Preview != string.Empty && s.Id != exceptSessionId)
            .Select(s => new { s.Id, s.StartedAt, s.TimeZone, Raw = s.Raw.DraftRevision, Cleaned = s.Cleaned.DraftRevision })
            .ToListAsync(cancellationToken);
        return new SummarySources(rows
            .Select(row => new SessionStub(row.Id, Session.JournalingDayOf(row.StartedAt, row.TimeZone), row.Raw, row.Cleaned))
            .Where(stub => stub.JournalingDay >= first && stub.JournalingDay < end));
    }

    public static Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, PeriodKey key, CancellationToken cancellationToken)
        => dbContext.LoadSourcesAsync(key.Start, key.End, cancellationToken);

    /// <summary>
    /// Removes each Summary of the User that can hold the words of a Session that a delete takes away, in the save of the delete.
    /// A Summary stays only when it is ready from the sources that stay: its fingerprint then names no Session of the delete.
    /// </summary>
    public static async Task RemoveSummariesThatCanHoldAsync(this AppDbContext dbContext, string sessionId, CancellationToken cancellationToken)
    {
        var summaries = await dbContext.Summaries
            .TagWithOperationCallSite("summaries.session_delete.load")
            .ToListAsync(cancellationToken);
        if (summaries.Count == 0)
            return;

        var sources = await dbContext.LoadSourcesAsync(summaries.Min(s => s.StartDate), summaries.Max(s => s.Key.End), cancellationToken, exceptSessionId: sessionId);
        dbContext.Summaries.RemoveRange(summaries.Where(s => s.StatusFor(sources.StateOf(s.Key)) != SummaryStatus.Ready));
    }

    /// <summary>Null for an ID that is no Summary ID, as for an ID of no row. The ID comes from a job payload.</summary>
    public static async Task<Summary?> FindByIdAsync(this IQueryable<Summary> summaries, string id, CancellationToken cancellationToken)
    {
        if (!EntityId.IsValid(id, Summary.IdPrefix))
            return null;

        return await summaries.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public static Task<Summary?> FindAsync(this IQueryable<Summary> summaries, PeriodKey key, CancellationToken cancellationToken)
        => summaries.FirstOrDefaultAsync(s => s.Period == key.Period && s.StartDate == key.Start, cancellationToken);
}
