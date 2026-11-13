namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class DeleteCorrection
{
    public sealed record Command(string Id, uint? IfVersion) : IRequest;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.correction_id", request.Id);

            var correction = await dbContext.Corrections
                .TagWithOperationCallSite("corrections.delete.load")
                .GetById(request.Id, cancellationToken);
            correction.GuardDelete(request.IfVersion);
            dbContext.Corrections.Remove(correction);
            await dbContext.SaveChangesAsync(cancellationToken);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.correction.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted correction {CorrectionId}, WAL scrubbed {WalScrubbed}", correction.Id, scrubbed);
        }
    }
}
