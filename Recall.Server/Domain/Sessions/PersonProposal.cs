namespace Recall.Server.Domain.Sessions;

using People;

public sealed record PersonProposal(string Label, IReadOnlyList<string> MatchedPersonIds)
{
    public bool Matches(Person person)
        => MatchedPersonIds.Contains(person.Id) || Person.KeyOf(person.Label) == Person.KeyOf(Label);

    /// <summary>
    /// SES-56: the People that an approve with no choice reads now. Each stored match that still exists comes first, in stored
    /// order, then each other Person of the name.
    /// </summary>
    public IReadOnlyList<Person> MatchesIn(IReadOnlyCollection<Person> people)
    {
        var key = Person.KeyOf(Label);
        return MatchedPersonIds.SelectMany(id => people.Where(person => person.Id == id))
            .Concat(people.Where(person => !MatchedPersonIds.Contains(person.Id) && Person.KeyOf(person.Label) == key))
            .ToList();
    }

    public static bool IsSettledBy(IReadOnlySet<string> taggedPersonIds, IReadOnlyList<Person> matches)
        => matches.Any(person => taggedPersonIds.Contains(person.Id));

    public bool Equals(PersonProposal? other)
        => other is not null && Label == other.Label && MatchedPersonIds.SequenceEqual(other.MatchedPersonIds);

    public override int GetHashCode()
        => MatchedPersonIds.Aggregate(Label.GetHashCode(StringComparison.Ordinal), (hash, id) => HashCode.Combine(hash, id));
}
