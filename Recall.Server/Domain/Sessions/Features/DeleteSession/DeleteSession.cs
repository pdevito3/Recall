namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Recordings;
using Recordings.Features;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Storage;
using Serilog;
using Summaries;

public static class DeleteSession
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.delete.load")
                .GetById(request.Id, cancellationToken);
            var recordings = await dbContext.Recordings
                .TagWithOperationCallSite("sessions.delete.recordings")
                .Where(r => r.SessionId == session.Id)
                .ToListAsync(cancellationToken);
            await dbContext.RemoveSummariesThatCanHoldAsync(session.Id, cancellationToken);
            dbContext.Recordings.RemoveRange(recordings);
            dbContext.Sessions.Remove(session);
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var recording in recordings)
                RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(recording), recording.Id, _logger);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.session.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted session {SessionId}, WAL scrubbed {WalScrubbed}", session.Id, scrubbed);
        }
    }
}
