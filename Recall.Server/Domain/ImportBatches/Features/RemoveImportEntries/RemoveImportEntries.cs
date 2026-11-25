namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class RemoveImportEntries
{
    public const string NullIdMessage = "Send each Client session ID as text.";

    public sealed record Command(string Id, ImportEntriesRemoveDto Dto) : IRequest<ImportRemoveResultDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, ImportRemoveResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportRemoveResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);
            ValidationException.MustNot(request.Dto.ClientSessionIds.Contains(null!), "clientSessionIds", NullIdMessage);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.remove.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            batch.EnsureOpen();

            var ids = request.Dto.ClientSessionIds.Distinct(StringComparer.Ordinal).ToList();
            var storable = ids.Where(StorableText.CanStore).ToList();
            var stored = await dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.remove.entries")
                .Where(e => e.BatchId == batch.Id && storable.Contains(e.ClientSessionId))
                .ToListAsync(cancellationToken);

            var remove = batch.Remove(ids, stored);
            dbContext.ImportEntries.RemoveRange(remove.Removed);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Removed {RemovedCount} of {IdCount} entries from import batch {ImportBatchId}, version {Version}",
                remove.Removed.Count, ids.Count, batch.Id, batch.Version);
            return remove.ToImportRemoveResultDto(batch);
        }
    }
}
