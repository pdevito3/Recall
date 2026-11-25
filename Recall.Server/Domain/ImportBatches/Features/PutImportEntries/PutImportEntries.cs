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

public static class PutImportEntries
{
    public const string NullEntryMessage = "Send each entry as an object with its fields.";

    public sealed record Command(string Id, ImportEntriesPutDto Dto) : IRequest<ImportPutResultDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, ImportPutResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportPutResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.put.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            batch.EnsureOpen();
            ValidationException.MustNot(request.Dto.Entries.Contains(null!), "entries", NullEntryMessage);
            ImportBatch.CheckPutCall(request.Dto.Entries.Select(e => e.ClientSessionId).ToList());

            var entries = request.Dto.Entries
                .Select(e => ImportPutEntry.Parse(e.ClientSessionId, () => ImportAgentLayer.Of(e.Date, e.Position, e.Time, e.TimeZone, e.Raw, e.Moods, e.Topics)))
                .ToList();
            var facts = await ImportPutFacts.LoadAsync(dbContext, batch, entries, cancellationToken);
            var put = batch.Put(entries, facts, timeProvider.GetUtcNow());
            await dbContext.ImportEntries.AddRangeAsync(put.Added, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Put {EntryCount} entries into import batch {ImportBatchId}, added {AddedCount}, version {Version}",
                entries.Count, batch.Id, put.Added.Count, batch.Version);
            return put.ToImportPutResultDto(batch);
        }
    }
}
