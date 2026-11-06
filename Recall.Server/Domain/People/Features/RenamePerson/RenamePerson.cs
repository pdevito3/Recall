namespace Recall.Server.Domain.People.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class RenamePerson
{
    public sealed record Command(string Id, PersonLabelDto Dto) : IRequest<PersonDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, PersonDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<PersonDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.person_id", request.Id);

            var person = await dbContext.People
                .TagWithOperationCallSite("people.rename.load")
                .GetById(request.Id, cancellationToken);
            person.Rename(request.Dto.Label);

            var sessions = await dbContext.Sessions
                .TagWithOperationCallSite("people.rename.sessions")
                .Where(s => s.MentionedPersonIds.Contains(person.Id))
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            foreach (var session in sessions)
                session.RenamePerson(person.Id, person.Label, now);

            await dbContext.SaveChangesAsync(cancellationToken);

            Activity.Current?.SetTag("app.person.session_count", sessions.Count);
            _logger.Here().Information("Renamed person {PersonId} in {SessionCount} sessions", person.Id, sessions.Count);
            return person.ToPersonDto();
        }
    }
}
