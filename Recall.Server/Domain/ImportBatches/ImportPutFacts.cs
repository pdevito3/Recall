namespace Recall.Server.Domain.ImportBatches;

using Databases;
using Microsoft.EntityFrameworkCore;

public sealed class ImportPutFacts
{
    private ImportPutFacts(IReadOnlyCollection<ImportEntry> stored, IReadOnlySet<string> sessionIds, IReadOnlySet<string> tombstoneIds)
    {
        Stored = stored;
        SessionIds = sessionIds;
        TombstoneIds = tombstoneIds;
    }

    public IReadOnlyCollection<ImportEntry> Stored { get; }

    public IReadOnlySet<string> SessionIds { get; }

    public IReadOnlySet<string> TombstoneIds { get; }

    public static async Task<ImportPutFacts> LoadAsync(
        AppDbContext dbContext, ImportBatch batch, IReadOnlyList<ImportPutEntry> entries, CancellationToken cancellationToken)
    {
        var ids = entries.Where(e => e.Agent is not null).Select(e => e.ClientSessionId).ToList();
        var dates = entries.Select(e => e.Agent?.Date).OfType<DateOnly>().Distinct().ToList();
        var sessionIds = await dbContext.Sessions
            .TagWithOperationCallSite("import_batches.put.sessions")
            .Where(s => ids.Contains(s.ClientSessionId))
            .Select(s => s.ClientSessionId)
            .ToListAsync(cancellationToken);
        var tombstoneIds = await dbContext.SyncChanges
            .TagWithOperationCallSite("import_batches.put.tombstones")
            .Where(c => c.IsTombstone && ids.Contains(c.ClientSessionId!))
            .Select(c => c.ClientSessionId!)
            .ToListAsync(cancellationToken);
        var stored = await dbContext.ImportEntries
            .TagWithOperationCallSite("import_batches.put.entries")
            .Where(e => ids.Contains(e.ClientSessionId) || (e.BatchId == batch.Id && dates.Contains(e.Agent.Date)))
            .ToListAsync(cancellationToken);
        return new ImportPutFacts(stored, sessionIds.ToHashSet(StringComparer.Ordinal), tombstoneIds.ToHashSet(StringComparer.Ordinal));
    }
}
