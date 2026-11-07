namespace Recall.Server.Domain.Summaries;

using Databases;
using Microsoft.EntityFrameworkCore;
using TimeZones;

public static class SummaryQueries
{
    /// <summary>
    /// The Sessions with words whose Journaling day is in the days from <paramref name="first"/> up to <paramref name="end"/>.
    /// A Session with no words is no source. It reads no words, so a year of Sessions is one small read.
    /// </summary>
    public static async Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, TimeZoneId zone, DateOnly first, DateOnly end, CancellationToken cancellationToken)
    {
        var from = new ZonedDate(first, zone).Start;
        var to = new ZonedDate(end, zone).Start;
        var rows = await dbContext.Sessions
            .TagWithOperationCallSite("summaries.sources")
            .AsNoTracking()
            .Where(s => s.StartedAt >= from && s.StartedAt < to && s.Preview != string.Empty)
            .Select(s => new { s.Id, s.StartedAt, Raw = s.Raw.DraftRevision, Cleaned = s.Cleaned.DraftRevision })
            .ToListAsync(cancellationToken);
        return new SummarySources(rows.Select(row => new SessionStub(row.Id, zone.ToLocal(row.StartedAt).Date, row.Raw, row.Cleaned)));
    }

    public static Task<SummarySources> LoadSourcesAsync(this AppDbContext dbContext, TimeZoneId zone, PeriodKey key, CancellationToken cancellationToken)
        => dbContext.LoadSourcesAsync(zone, key.Start, key.End, cancellationToken);

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
