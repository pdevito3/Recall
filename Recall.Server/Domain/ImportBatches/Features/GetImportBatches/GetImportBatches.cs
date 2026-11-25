namespace Recall.Server.Domain.ImportBatches.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetImportBatches
{
    public sealed record Query : IRequest<IReadOnlyList<ImportBatchDto>>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, IReadOnlyList<ImportBatchDto>>
    {
        public async Task<IReadOnlyList<ImportBatchDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();

            var batches = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.list")
                .AsNoTracking()
                .OrderByDescending(b => b.OpenedAt).ThenByDescending(b => b.Id)
                .ToListAsync(cancellationToken);
            var counts = await dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.list.counts")
                .GroupBy(e => e.BatchId)
                .Select(g => new { BatchId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.BatchId, g => g.Count, cancellationToken);
            return batches.Select(b => b.ToImportBatchDto(counts.GetValueOrDefault(b.Id))).ToList();
        }
    }
}
