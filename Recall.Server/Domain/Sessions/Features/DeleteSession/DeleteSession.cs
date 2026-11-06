namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class DeleteSession
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.delete.load")
                .GetById(request.Id, cancellationToken);
            dbContext.Sessions.Remove(session);
            await dbContext.SaveChangesAsync(cancellationToken);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.session.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted session {SessionId}, WAL scrubbed {WalScrubbed}", session.Id, scrubbed);
        }
    }
}
