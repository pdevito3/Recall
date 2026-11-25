namespace Recall.Server.Domain.Sessions.Features;

using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TimeZones;

public static class GetJournalDays
{
    public const int MaxDays = 366;
    public const string RangeMessage = "Pick a range of at most 366 days, with the end on or after the start.";

    // A Session in any zone can start up to one day before or after the UTC day of its Journaling day
    private static readonly TimeSpan AnyZone = TimeSpan.FromDays(1);

    public sealed record Query(DateOnly From, DateOnly To) : IRequest<IReadOnlyList<JournalDayDto>>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, IReadOnlyList<JournalDayDto>>
    {
        public async Task<IReadOnlyList<JournalDayDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            var (first, last) = (request.From, request.To);
            ValidationException.Must(last >= first && last.DayNumber - first.DayNumber < MaxDays, "to", RangeMessage);

            var from = new ZonedDate(first, TimeZoneId.Utc).Start - AnyZone;
            var to = new ZonedDate(last.AddDays(1), TimeZoneId.Utc).Start + AnyZone;
            var starts = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.journal_days")
                .Where(s => s.StartedAt >= from && s.StartedAt < to)
                .Select(s => new { s.StartedAt, s.TimeZone })
                .ToListAsync(cancellationToken);

            return starts
                .Select(s => Session.JournalingDayOf(s.StartedAt, s.TimeZone))
                .Where(day => day >= first && day <= last)
                .GroupBy(day => day)
                .OrderBy(group => group.Key)
                .Select(group => new JournalDayDto { Date = group.Key, SessionCount = group.Count() })
                .ToList();
        }
    }
}
