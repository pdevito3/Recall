namespace Recall.Server.Domain.Sessions.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UserSettings;

public static class GetSessionPage
{
    public sealed record Query(CursorPageParametersDto Parameters) : IRequest<SessionPageDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, SessionPageDto>
    {
        public async Task<SessionPageDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();

            var sessions = dbContext.Sessions
                .TagWithOperationCallSite("sessions.page")
                .AsNoTracking();
            if (request.Parameters.Cursor is { } cursorText)
            {
                sessions = OlderThan(sessions, SessionCursor.Parse(cursorText));
            }

            var (page, hasMore) = await sessions
                .OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
                .Select(s => new
                {
                    s.Id,
                    s.ClientSessionId,
                    s.StartedAt,
                    s.Preview,
                    s.Synopsis,
                    s.CleanupRun,
                    s.CleanupFailure,
                    s.Raw.DraftRevision,
                    s.LastCleanupRawRevision,
                    s.Activity,
                    s.Moods,
                    s.UserTopics,
                    s.AiTopics,
                    s.MentionedPersonIds,
                    s.Version,
                    s.MetadataVersion,
                })
                .ToKeysetPageAsync(request.Parameters.PageSize, cancellationToken);

            var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
            return new SessionPageDto
            {
                Items = page.Select(row => new SessionSummaryDto
                {
                    Id = row.Id,
                    ClientSessionId = row.ClientSessionId,
                    StartedAt = row.StartedAt,
                    JournalingDay = zone.ToLocal(row.StartedAt).Date,
                    Preview = row.Preview,
                    Synopsis = row.Synopsis,
                    CleanupStatus = Session.CleanupStatusOf(row.CleanupRun, row.DraftRevision, row.LastCleanupRawRevision).ToString(),
                    CleanupFailure = row.CleanupFailure.ToString(),
                    Activity = row.Activity,
                    Moods = row.Moods,
                    Topics = SessionMapper.TopicsOf(row.UserTopics, row.AiTopics),
                    PersonIds = row.MentionedPersonIds,
                    Version = row.Version,
                    MetadataVersion = row.MetadataVersion,
                }).ToList(),
                NextCursor = hasMore ? new SessionCursor(page[^1].StartedAt, page[^1].Id).ToString() : null,
            };
        }

        private static IQueryable<Session> OlderThan(IQueryable<Session> sessions, SessionCursor cursor)
            => sessions.Where(s => s.StartedAt <= cursor.StartedAt
                && (s.StartedAt < cursor.StartedAt || s.Id.CompareTo(cursor.Id) < 0));
    }
}
