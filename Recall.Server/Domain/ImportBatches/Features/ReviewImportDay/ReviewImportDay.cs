namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class ReviewImportDay
{
    public sealed record Command(string Id, ImportDayReviewDto Dto) : IRequest<ImportDayReviewedDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, ImportDayReviewedDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportDayReviewedDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.review_day.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, cancellationToken);
            var changed = batch.ReviewDay(request.Dto.Date, request.Dto.Rejected);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Reviewed a day of import batch {ImportBatchId}, changed {Changed}, rejected {Rejected}",
                batch.Id, changed, request.Dto.Rejected);
            return new ImportDayReviewedDto { Version = batch.Version, Date = request.Dto.Date, Rejected = request.Dto.Rejected };
        }
    }
}
