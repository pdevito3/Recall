namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Resources;
using Serilog;

public static class OpenImportBatch
{
    public sealed record Command(ImportBatchForCreationDto Dto) : IRequest<ImportBatchDto>;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, ImportBatchDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportBatchDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            var owner = dbContext.RequireJournalOwner();

            var batch = ImportBatch.Open(owner.TenantId, owner.UserId, request.Dto.Name, request.Dto.TimeZone, timeProvider.GetUtcNow());
            Activity.Current?.SetTag("app.import_batch_id", batch.Id);
            await dbContext.ImportBatches.AddAsync(batch, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Opened import batch {ImportBatchId}", batch.Id);
            return batch.ToImportBatchDto(0);
        }
    }
}
