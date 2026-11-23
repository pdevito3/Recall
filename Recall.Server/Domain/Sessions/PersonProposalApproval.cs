namespace Recall.Server.Domain.Sessions;

using People;

public abstract record PersonProposalApproval
{
    public const string AskWhichField = "bindToPersonId";
    public const string AskWhichMessage = "Two or more People have this name. Choose the person to tag, or add a new person.";

    private PersonProposalApproval() { }

    public sealed record Bind(Person Person) : PersonProposalApproval;

    public sealed record Create(NewPersonKind Kind) : PersonProposalApproval;

    public sealed record AskWhich(IReadOnlyList<string> PersonIds) : PersonProposalApproval;

    public static PersonProposalApproval Decide(PersonProposal proposal, Person? chosen, bool createNew, IReadOnlyCollection<Person> people)
    {
        if (chosen is not null)
            return new Bind(chosen);
        if (createNew)
            return new Create(NewPersonKind.AskedFor);

        var candidates = people
            .Where(person => proposal.MatchedPersonIds.Contains(person.Id) || Person.KeyOf(person.Label) == Person.KeyOf(proposal.Label))
            .ToList();
        return candidates.Count switch
        {
            0 => new Create(NewPersonKind.ProposedName),
            1 => new Bind(candidates[0]),
            _ => new AskWhich(candidates.Select(person => person.Id).ToList()),
        };
    }
}

public enum NewPersonKind
{
    ProposedName,

    AskedFor,
}
