namespace Recall.Server.Domain.Sessions;

using People;

/// <summary>
/// What an approve of a People proposal tags. The directory can change after the Cleanup: a person can delete a match, rename it,
/// or add a second Person of the name. So the live directory decides, and the person chooses between two or more People.
/// </summary>
public abstract record PersonProposalApproval
{
    public const string AskWhichField = "bindToPersonId";
    public const string AskWhichMessage = "Two or more People have this name. Choose the person to tag, or add a new person.";

    private PersonProposalApproval() { }

    /// <summary>Tag this Person.</summary>
    public sealed record Bind(Person Person) : PersonProposalApproval;

    /// <summary>Add a Person of the proposed name and tag it.</summary>
    public sealed record Create(NewPersonKind Kind) : PersonProposalApproval;

    /// <summary>Tag nothing, because only the person can choose between these People.</summary>
    public sealed record AskWhich(IReadOnlyList<string> PersonIds) : PersonProposalApproval;

    /// <param name="proposal">The proposal that the person approves.</param>
    /// <param name="chosen">The Person of <c>bindToPersonId</c>, or null when the request names none.</param>
    /// <param name="createNew">The person asked for a second Person of this name.</param>
    /// <param name="people">The live directory of the owner. It is read only when the request makes no choice.</param>
    public static PersonProposalApproval Decide(PersonProposal proposal, Person? chosen, bool createNew, IReadOnlyCollection<Person> people)
    {
        if (chosen is not null)
            return new Bind(chosen);
        if (createNew)
            return new Create(NewPersonKind.AskedFor);

        // A match that the person sees on the proposal stays a match after a rename.
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
    /// <summary>One Person of each proposed name for a User, so approvals of one new name at the same time add one Person.</summary>
    ProposedName,

    /// <summary>A real choice of another Person of a name that the directory can hold already.</summary>
    AskedFor,
}
