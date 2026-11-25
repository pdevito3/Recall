namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Microsoft.EntityFrameworkCore;

public static class ImportBatchQueries
{
    public static async Task<ImportBatch> GetBatchAsync(this IQueryable<ImportBatch> batches, string id, CancellationToken cancellationToken)
        => (EntityId.IsValid(id, ImportBatch.IdPrefix) ? await batches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken) : null)
           ?? throw new NotFoundException(ImportBatch.NotFoundMessage);
}
