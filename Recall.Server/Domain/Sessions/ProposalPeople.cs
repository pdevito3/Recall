namespace Recall.Server.Domain.Sessions;

using Databases;
using Microsoft.EntityFrameworkCore;
using People;

/// <summary>
/// The People that the proposals of some Sessions can match. The Session mapper requires it, so that no read of a Session sends
/// the matches of the Cleanup in place of the matches of now (SES-56).
/// </summary>
public sealed class ProposalPeople
{
    private ProposalPeople(IReadOnlyCollection<Person> people) => People = people;

    public IReadOnlyCollection<Person> People { get; }

    /// <summary>Reads the People of the owner once, and only when one of the Sessions has a proposal.</summary>
    public static async Task<ProposalPeople> LoadAsync(AppDbContext dbContext, IEnumerable<Session> sessions, CancellationToken cancellationToken)
    {
        if (!sessions.Any(session => session.PeopleProposals.Count > 0))
            return new ProposalPeople([]);

        var people = await dbContext.People.AsNoTracking()
            .TagWithOperationCallSite("sessions.proposal_people")
            .ToListAsync(cancellationToken);
        return new ProposalPeople(people);
    }

    public static ProposalPeople Of(IReadOnlyCollection<Person> people) => new(people);
}
