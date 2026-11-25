namespace Recall.Server.Domain.ImportBatches.Features;

using System.Diagnostics;
using AuditEntries;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class AcceptImportBatch
{
    public const string ReviewMessage = "The import review is on, so only you can accept this batch, in Recall in a browser.";

    public sealed record Command(string Id, ImportBatchAcceptDto Dto) : IRequest<ImportBatchDto>;

    public sealed class Handler(AppDbContext dbContext, ICurrentUserService currentUserService, TimeProvider timeProvider)
        : IRequestHandler<Command, ImportBatchDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ImportBatchDto> Handle(Command request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalWriter();
            var owner = dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.import_batch_id", request.Id);

            return await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                for (var run = 1; ; run++)
                {
                    try
                    {
                        return await AcceptAsync(request, ct);
                    }
                    catch (StaleVersionException raced)
                    {
                        dbContext.DiscardLostRace();
                        var stored = await dbContext.ImportBatches
                            .TagWithOperationCallSite("import_batches.accept.reload")
                            .AsNoTracking()
                            .GetBatchAsync(request.Id, ct);
                        if (stored.State == ImportBatchState.Accepted)
                            return stored.ToImportBatchDto(0);
                        if (run > 1 || stored.Version != request.Dto.Version)
                            throw new StaleVersionException(ImportBatch.ChangedMessage, raced);

                        Telemetry.AddEvent([("app.race.run", run)], "race.lost");
                    }
                }
            }, cancellationToken);
        }

        private async Task<ImportBatchDto> AcceptAsync(Command request, CancellationToken ct)
        {
            var batch = await dbContext.ImportBatches
                .TagWithOperationCallSite("import_batches.accept.load")
                .Include(b => b.Days)
                .GetBatchAsync(request.Id, ct);
            if (batch.State == ImportBatchState.Accepted)
                return batch.ToImportBatchDto(0);

            var setting = await dbContext.UserSettings
                .TagWithOperationCallSite("import_batches.accept.settings")
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);
            if ((setting?.RequireImportReview ?? true) && !SessionClaims.IsWebSession(currentUserService.User))
                throw new ForbiddenAccessException(ReviewMessage);

            var entries = await dbContext.ImportEntries
                .TagWithOperationCallSite("import_batches.accept.entries")
                .Where(e => e.BatchId == batch.Id)
                .ToListAsync(ct);
            var ids = entries.Select(e => e.ClientSessionId).ToList();
            var sessionIds = await dbContext.Sessions
                .TagWithOperationCallSite("import_batches.accept.sessions")
                .Where(s => ids.Contains(s.ClientSessionId))
                .Select(s => s.ClientSessionId)
                .ToListAsync(ct);
            var tombstoneIds = await dbContext.SyncChanges
                .TagWithOperationCallSite("import_batches.accept.tombstones")
                .Where(c => c.IsTombstone && ids.Contains(c.ClientSessionId!))
                .Select(c => c.ClientSessionId!)
                .ToListAsync(ct);

            var accept = batch.Accept(request.Dto.Version, entries, sessionIds.ToHashSet(StringComparer.Ordinal),
                tombstoneIds.ToHashSet(StringComparer.Ordinal), timeProvider.GetUtcNow());
            await dbContext.Sessions.AddRangeAsync(accept.Sessions, ct);
            dbContext.ImportEntries.RemoveRange(accept.Removed);
            var result = batch.Result;
            dbContext.RecordAction(new ImportBatchAccepted(AuditSubject.Of(batch), result.SessionsMade, result.EntriesRejected,
                result.DaysRejected, result.SkippedInJournal, result.SkippedDeleted));
            await dbContext.SaveChangesAsync(ct);

            _logger.Here().Information(
                "Accepted import batch {ImportBatchId}: made {SessionsMade} sessions, rejected {EntriesRejected} entries and {DaysRejected} days, skipped {SkippedInJournal} in the journal and {SkippedDeleted} deleted",
                batch.Id, result.SessionsMade, result.EntriesRejected, result.DaysRejected, result.SkippedInJournal, result.SkippedDeleted);
            return batch.ToImportBatchDto(0);
        }
    }
}
