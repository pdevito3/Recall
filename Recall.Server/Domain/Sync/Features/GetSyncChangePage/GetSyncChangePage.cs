namespace Recall.Server.Domain.Sync.Features;

using Corrections.Mappings;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using People.Mappings;
using Sessions;
using Sessions.Dtos;
using Sessions.Mappings;
using UserSettings.Mappings;

public static class GetSyncChangePage
{
    public sealed record Query(CursorPageParametersDto Parameters) : IRequest<SyncChangePageDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, SyncChangePageDto>
    {
        public async Task<SyncChangePageDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            var after = SyncCursor.Parse(request.Parameters.Cursor);

            var (page, hasMore) = await dbContext.SyncChanges
                .TagWithOperationCallSite("sync.page")
                .AsNoTracking()
                .Where(c => c.Sequence > after)
                .OrderBy(c => c.Sequence)
                .ToKeysetPageAsync(request.Parameters.PageSize, cancellationToken);

            if (page.Count == 0)
            {
                await ThrowWhenCursorPredatesARestoreAsync(after, cancellationToken);
                return new SyncChangePageDto { Cursor = SyncCursor.Of(after) };
            }

            var last = page[^1].Sequence;
            var live = page.Where(c => !c.IsTombstone).Select(c => c.EntityType).ToHashSet();
            return new SyncChangePageDto
            {
                Cursor = SyncCursor.Of(last),
                HasMore = hasMore,
                Sessions = live.Contains(SyncEntityType.Session)
                    ? (await RowsAsync(dbContext.Sessions, SyncEntityType.Session, "sync.sessions", after, last, cancellationToken))
                        .Select(s => s.ToSessionDto()).ToList()
                    : [],
                People = live.Contains(SyncEntityType.Person)
                    ? (await RowsAsync(dbContext.People, SyncEntityType.Person, "sync.people", after, last, cancellationToken))
                        .Select(p => p.ToPersonDto()).ToList()
                    : [],
                Corrections = live.Contains(SyncEntityType.Correction)
                    ? (await RowsAsync(dbContext.Corrections, SyncEntityType.Correction, "sync.corrections", after, last, cancellationToken))
                        .Select(c => c.ToCorrectionDto()).ToList()
                    : [],
                Settings = live.Contains(SyncEntityType.UserSettings)
                    ? (await RowsAsync(dbContext.UserSettings, SyncEntityType.UserSettings, "sync.settings", after, last, cancellationToken))
                        .Select(s => s.ToUserSettingDto()).FirstOrDefault()
                    : null,
                Tombstones = page.Where(c => c.IsTombstone)
                    .Select(c => new SyncTombstoneDto { EntityType = c.EntityType.ToString(), Id = c.EntityId, ClientSessionId = c.ClientSessionId })
                    .ToList(),
            };
        }

        private Task<List<TEntity>> RowsAsync<TEntity>(DbSet<TEntity> table, SyncEntityType type, string operation, long after, long last,
            CancellationToken cancellationToken)
            where TEntity : BaseEntity
            => dbContext.SyncChanges
                .TagWithOperationCallSite(operation)
                .AsNoTracking()
                .Where(c => c.EntityType == type && c.Sequence > after && c.Sequence <= last)
                .Join(table, c => c.EntityId, row => row.Id, (_, row) => row)
                .ToListAsync(cancellationToken);

        private async Task ThrowWhenCursorPredatesARestoreAsync(long after, CancellationToken cancellationToken)
        {
            if (after == SyncCursor.Start)
                return;

            var newest = await dbContext.SyncChanges
                .TagWithOperationCallSite("sync.newest")
                .MaxAsync(c => (long?)c.Sequence, cancellationToken);
            if (after > (newest ?? SyncCursor.Start))
                throw new ValidationException("cursor", "The cursor is ahead of the server. Drop the synced rows and pull from the start.");
        }
    }
}
