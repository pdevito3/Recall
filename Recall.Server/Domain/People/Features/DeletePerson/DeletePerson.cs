namespace Recall.Server.Domain.People.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class DeletePerson
{
    /// <summary>
    /// The delete also rewrites each Session that mentions the Person, so a Draft save during it runs it again. <c>IfVersion</c>
    /// is the version of the Person that the caller saw, and it gives 409 when a rename moved it.
    /// </summary>
    public sealed record Command(string Id, uint? IfVersion) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider, WalScrub walScrub) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.person_id", request.Id);

            var person = await dbContext.People
                .TagWithOperationCallSite("people.delete.load")
                .GetById(request.Id, cancellationToken);
            person.GuardDelete(request.IfVersion);

            var sessions = await dbContext.Sessions
                .TagWithOperationCallSite("people.delete.sessions")
                .Where(s => s.MentionedPersonIds.Contains(person.Id))
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            foreach (var session in sessions)
                session.UnlinkPerson(person.Id, now);

            dbContext.People.Remove(person);
            await dbContext.SaveChangesAsync(cancellationToken);

            var scrubbed = await walScrub.TruncateAsync(dbContext.Database.GetConnectionString()!);
            Activity.Current?.SetTag("app.person.session_count", sessions.Count);
            Activity.Current?.SetTag("app.person.wal_scrubbed", scrubbed);
            _logger.Here().Information("Deleted person {PersonId} from {SessionCount} sessions, WAL scrubbed {WalScrubbed}", person.Id, sessions.Count, scrubbed);
        }
    }
}
