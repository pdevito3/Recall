namespace Recall.Server.Domain.Summaries;

using Databases;
using Microsoft.EntityFrameworkCore;
using TimeZones;
using UserSettings;

public static class SummaryQueries
{
    /// <summary>
    /// The Sessions with words whose Journaling day is in the days from <paramref name="first"/> up to <paramref name="end"/>.
    /// A Session with no words is no source. It reads no words, so a year of Sessions is one small read.
    /// </summary>
    public static async Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, TimeZoneId zone, DateOnly first, DateOnly end, CancellationToken cancellationToken, string? exceptSessionId = null)
    {
        var from = new ZonedDate(first, zone).Start;
        var to = new ZonedDate(end, zone).Start;
        var rows = await dbContext.Sessions
            .TagWithOperationCallSite("summaries.sources")
            .AsNoTracking()
            .Where(s => s.StartedAt >= from && s.StartedAt < to && s.Preview != string.Empty && s.Id != exceptSessionId)
            .Select(s => new { s.Id, s.StartedAt, Raw = s.Raw.DraftRevision, Cleaned = s.Cleaned.DraftRevision })
            .ToListAsync(cancellationToken);
        return new SummarySources(rows.Select(row => new SessionStub(row.Id, zone.ToLocal(row.StartedAt).Date, row.Raw, row.Cleaned)));
    }

    public static Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, TimeZoneId zone, PeriodKey key, CancellationToken cancellationToken)
        => dbContext.LoadSourcesAsync(zone, key.Start, key.End, cancellationToken);

    /// <summary>
    /// Removes each Summary of the User that can hold the words of a Session that a delete takes away, in the save of the delete.
    /// A Summary stays only when it is ready from the sources that stay: its fingerprint then names no Session of the delete. A
    /// move to another day or a change of the zone can have put the Session in any period, so the check reads each Summary.
    /// </summary>
    public static async Task RemoveSummariesThatCanHoldAsync(this AppDbContext dbContext, string sessionId, CancellationToken cancellationToken)
    {
        var summaries = await dbContext.Summaries
            .TagWithOperationCallSite("summaries.session_delete.load")
            .ToListAsync(cancellationToken);
        if (summaries.Count == 0)
            return;

        var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
        var sources = await dbContext.LoadSourcesAsync(zone, summaries.Min(s => s.StartDate), summaries.Max(s => s.Key.End), cancellationToken, exceptSessionId: sessionId);
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
