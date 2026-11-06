namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using People;
using Resources;
using Serilog;
using UserSettings;

public static class RespondToPersonProposal
{
    public sealed record Command(string Id, PersonProposalResponseDto Dto) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, SessionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.respond_to_person_proposal.load")
                .GetById(request.Id, cancellationToken);

            // A retry of a response that reached the server finds no proposal, and it gets the stored Session.
            if (session.ProposalFor(request.Dto.Label) is { } proposal)
            {
                if (request.Dto.Approve)
                {
                    var person = await PersonForAsync(session, proposal, request.Dto, cancellationToken);
                    session.ApprovePersonProposal(proposal, person.Id, person.Label, timeProvider.GetUtcNow());
                }
                else
                {
                    session.DropPersonProposal(proposal);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.Here().Information("Took a response to a people proposal of session {SessionId}, approve {Approve}", session.Id, request.Dto.Approve);
            }

            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }

        private async Task<Person> PersonForAsync(Session session, PersonProposal proposal, PersonProposalResponseDto dto, CancellationToken cancellationToken)
        {
            if (dto.BindToPersonId is { } chosen)
            {
                return await dbContext.People
                    .TagWithOperationCallSite("sessions.respond_to_person_proposal.chosen")
                    .GetById(chosen, cancellationToken);
            }

            // A person can delete the match after the Cleanup, and the words of the proposal still name someone.
            var match = dto.CreateNew || proposal.MatchedPersonId is null
                ? null
                : await dbContext.People
                    .TagWithOperationCallSite("sessions.respond_to_person_proposal.match")
                    .FirstOrDefaultAsync(person => person.Id == proposal.MatchedPersonId, cancellationToken);
            if (match is not null)
                return match;

            var person = Person.Create(session.TenantId, session.UserId, proposal.Label);
            await dbContext.People.AddAsync(person, cancellationToken);
            return person;
        }
    }
}
