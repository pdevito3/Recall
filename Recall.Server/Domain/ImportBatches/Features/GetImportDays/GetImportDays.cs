namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetImportDays
{
    public sealed record Query(string Id) : IRequest<ImportDaysDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, ImportDaysDto>
    {
        public async Task<ImportDaysDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.days.batch")
                .AsNoTracking()
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            var counts = await dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.days.counts")
                .Where(e => e.BatchId == batch.Id)
                .GroupBy(e => e.Agent.Date)
                .Select(g => new { Date = g.Key, EntryCount = g.Count(), RejectedCount = g.Count(e => e.Review.Rejected) })
                .ToDictionaryAsync(g => g.Date, cancellationToken);
            var rejectedDays = batch.Days.Where(d => d.Rejected).Select(d => d.Date).ToHashSet();

            return new ImportDaysDto
            {
                Version = batch.Version,
                Days = counts.Keys.Union(rejectedDays).Order()
                    .Select(date => new ImportDayDto
                    {
                        Date = date,
                        EntryCount = counts.GetValueOrDefault(date)?.EntryCount ?? 0,
                        RejectedCount = counts.GetValueOrDefault(date)?.RejectedCount ?? 0,
                        Rejected = rejectedDays.Contains(date),
                    })
                    .ToList(),
            };
        }
    }
}
