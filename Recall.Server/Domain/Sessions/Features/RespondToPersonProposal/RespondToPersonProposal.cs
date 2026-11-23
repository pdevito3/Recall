namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using People;
using Resources;
using Serilog;

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

            return session.ToSessionDto();
        }

        private async Task<Person> PersonForAsync(Session session, PersonProposal proposal, PersonProposalResponseDto dto, CancellationToken cancellationToken)
        {
            var chosen = dto.BindToPersonId is { } id
                ? await dbContext.People
                    .TagWithOperationCallSite("sessions.respond_to_person_proposal.chosen")
                    .GetById(id, cancellationToken)
                : null;
            var people = chosen is null && !dto.CreateNew
                ? await dbContext.People.AsNoTracking()
                    .TagWithOperationCallSite("sessions.respond_to_person_proposal.match")
                    .ToListAsync(cancellationToken)
                : [];

            switch (PersonProposalApproval.Decide(proposal, chosen, dto.CreateNew, people))
            {
                case PersonProposalApproval.Bind bind:
                    return bind.Person;
                case PersonProposalApproval.Create create:
                    var person = create.Kind == NewPersonKind.AskedFor
                        ? Person.Create(session.TenantId, session.UserId, proposal.Label)
                        : Person.CreateFromProposedName(session.TenantId, session.UserId, proposal.Label);
                    await dbContext.People.AddAsync(person, cancellationToken);
                    return person;
                case PersonProposalApproval.AskWhich ask:
                    Activity.Current?.SetTag("app.person_proposal.match_count", ask.PersonIds.Count);
                    throw new ValidationException(PersonProposalApproval.AskWhichField, PersonProposalApproval.AskWhichMessage);
                default:
                    throw new InvalidOperationException("An approval has a case that the handler does not know.");
            }
        }
    }
}
