namespace Recall.Server.Domain.Sessions;

using People;

public sealed record PersonProposal(string Label, IReadOnlyList<string> MatchedPersonIds)
{
    public bool Matches(Person person)
        => MatchedPersonIds.Contains(person.Id) || Person.KeyOf(person.Label) == Person.KeyOf(Label);

    public bool IsSettledBy(IReadOnlySet<string> taggedPersonIds) => MatchedPersonIds.Any(taggedPersonIds.Contains);

    public bool Equals(PersonProposal? other)
        => other is not null && Label == other.Label && MatchedPersonIds.SequenceEqual(other.MatchedPersonIds);

    public override int GetHashCode()
        => MatchedPersonIds.Aggregate(Label.GetHashCode(StringComparison.Ordinal), (hash, id) => HashCode.Combine(hash, id));
}
