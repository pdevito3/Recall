namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetImportEntries
{
    /// <summary>The read of the review in Recall. Each entry has its document (IMP-32).</summary>
    public sealed record Query(string Id, DateOnly? From, DateOnly? To) : IRequest<ImportEntryPageDto>;

    /// <summary>The read of an agent. The agent sent Raw, so the reply has no document and makes none.</summary>
    public sealed record AgentQuery(string Id, DateOnly? From, DateOnly? To) : IRequest<ImportEntryPageForAgentDto>;

    public sealed class Handler(AppDbContext dbContext)
        : IRequestHandler<Query, ImportEntryPageDto>, IRequestHandler<AgentQuery, ImportEntryPageForAgentDto>
    {
        public async Task<ImportEntryPageDto> Handle(Query request, CancellationToken cancellationToken)
        {
            var (batch, entries, rejectedDays) = await ReadAsync(request.Id, request.From, request.To, cancellationToken);
            return new ImportEntryPageDto
            {
                Version = batch.Version,
                Entries = entries.Select(e => e.ToImportEntryDto(batch)).ToList(),
                RejectedDays = rejectedDays,
            };
        }

        public async Task<ImportEntryPageForAgentDto> Handle(AgentQuery request, CancellationToken cancellationToken)
        {
            var (batch, entries, rejectedDays) = await ReadAsync(request.Id, request.From, request.To, cancellationToken);
            return new ImportEntryPageForAgentDto
            {
                Version = batch.Version,
                Entries = entries.Select(e => e.ToImportEntryForAgentDto(batch)).ToList(),
                RejectedDays = rejectedDays,
            };
        }

        private async Task<(ImportBatch Batch, List<ImportEntry> Entries, List<DateOnly> RejectedDays)> ReadAsync(
            string id, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.entries.batch")
                .AsNoTracking()
                .Include(b => b.Days)
                .GetBatchAsync(id, cancellationToken);
            var query = dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.entries.list")
                .AsNoTracking()
                .Where(e => e.BatchId == batch.Id);
            if (from is { } first)
                query = query.Where(e => e.Agent.Date >= first);
            if (to is { } last)
                query = query.Where(e => e.Agent.Date <= last);
            var entries = await query
                .OrderBy(e => e.Agent.Date).ThenBy(e => e.Agent.Position)
                .ToListAsync(cancellationToken);
            var rejectedDays = batch.Days
                .Where(d => d.Rejected && (from is null || d.Date >= from) && (to is null || d.Date <= to))
                .Select(d => d.Date)
                .Order()
                .ToList();
            return (batch, entries, rejectedDays);
        }
    }
}
