namespace Recall.Server.Domain.Sessions;

/// <summary>
/// A name that Cleanup proposes as a mention, with each Person of the directory whose label matched it when the Cleanup ran.
/// No ID means a new name. Two or more IDs mean that only the person can choose which Person the name is.
/// </summary>
public sealed record PersonProposal(string Label, IReadOnlyList<string> MatchedPersonIds)
{
    public bool Equals(PersonProposal? other)
        => other is not null && Label == other.Label && MatchedPersonIds.SequenceEqual(other.MatchedPersonIds);

    public override int GetHashCode()
        => MatchedPersonIds.Aggregate(Label.GetHashCode(StringComparison.Ordinal), (hash, id) => HashCode.Combine(hash, id));
}
