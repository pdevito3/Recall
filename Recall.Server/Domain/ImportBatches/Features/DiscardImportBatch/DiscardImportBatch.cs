namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class DiscardImportBatch
{
    public sealed record Command(string Id) : IRequest;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.discard.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            batch.EnsureOpen();
            var entries = await dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.discard.entries")
                .Where(e => e.BatchId == batch.Id)
                .ToListAsync(cancellationToken);
            dbContext.ImportEntries.RemoveRange(entries);
            dbContext.ImportBatches.Remove(batch);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Discarded import batch {ImportBatchId} with {EntryCount} entries", batch.Id, entries.Count);
        }
    }
}
