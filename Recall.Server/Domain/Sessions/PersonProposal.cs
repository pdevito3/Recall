namespace Recall.Server.Domain.Sessions;

public sealed record PersonProposal(string Label, IReadOnlyList<string> MatchedPersonIds)
{
    public bool Equals(PersonProposal? other)
        => other is not null && Label == other.Label && MatchedPersonIds.SequenceEqual(other.MatchedPersonIds);

    public override int GetHashCode()
        => MatchedPersonIds.Aggregate(Label.GetHashCode(StringComparison.Ordinal), (hash, id) => HashCode.Combine(hash, id));
}
