namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetImportEntries
{
    /// <param name="Id">The ID of the batch.</param>
    /// <param name="From">The first date to read, or null for the first entry.</param>
    /// <param name="To">The last date to read, or null for the last entry.</param>
    public sealed record Query(string Id, DateOnly? From, DateOnly? To) : IRequest<ImportEntryPageDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, ImportEntryPageDto>
    {
        public async Task<ImportEntryPageDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.entries.batch")
                .AsNoTracking()
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            var query = dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.entries.list")
                .AsNoTracking()
                .Where(e => e.BatchId == batch.Id);
            if (request.From is { } from)
                query = query.Where(e => e.Agent.Date >= from);
            if (request.To is { } to)
                query = query.Where(e => e.Agent.Date <= to);
            var entries = await query
                .OrderBy(e => e.Agent.Date).ThenBy(e => e.Agent.Position)
                .ToListAsync(cancellationToken);

            return new ImportEntryPageDto
            {
                Version = batch.Version,
                Entries = entries.Select(e => e.ToImportEntryDto(batch)).ToList(),
                RejectedDays = batch.Days
                    .Where(d => d.Rejected && (request.From is null || d.Date >= request.From) && (request.To is null || d.Date <= request.To))
                    .Select(d => d.Date)
                    .Order()
                    .ToList(),
            };
        }
    }
}
