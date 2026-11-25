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

public static class ReviewImportEntry
{
    public sealed record Command(string Id, ImportEntryReviewDto Dto) : IRequest<ImportEntryReviewedDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, ImportEntryReviewedDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportEntryReviewedDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);
            var dto = request.Dto;

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.review_entry.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            batch.EnsureOpen();
            var review = ImportReview.Of(dto.Rejected, dto.Words, dto.Time);
            var entry = StorableText.CanStore(dto.ClientSessionId)
                ? await dbContext.ImportEntries
                    .TagWithOperationCallSite("import_batches.review_entry.entry")
                    .FirstOrDefaultAsync(e => e.BatchId == batch.Id && e.ClientSessionId == dto.ClientSessionId, cancellationToken)
                : null;
            if (entry is null)
                throw new NotFoundException(ImportEntry.NotInBatchMessage);

            var changed = entry.ChangeReview(review, batch, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Reviewed entry {ImportEntryId} of import batch {ImportBatchId}, changed {Changed}, rejected {Rejected}",
                entry.Id, batch.Id, changed, review.Rejected);
            return new ImportEntryReviewedDto { Version = batch.Version, Entry = entry.ToImportEntryDto(batch) };
        }
    }
}
