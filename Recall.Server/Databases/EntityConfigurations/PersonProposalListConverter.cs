namespace Recall.Server.Databases.EntityConfigurations;

using System.Text.Json;
using Domain.Sessions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class PersonProposalListConverter()
    : ValueConverter<IReadOnlyList<PersonProposal>, string>(
        list => JsonSerializer.Serialize(list, Web),
        json => JsonSerializer.Deserialize<List<PersonProposal>>(json, Web) ?? new List<PersonProposal>())
{
    // Ten labels of the longest name, with an escape for each character, and an ID and the field names for each.
    public const int ColumnMaxLength = 10 * (200 * 6 + 100);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly ValueComparer<IReadOnlyList<PersonProposal>> Comparer = new(
        (left, right) => left!.SequenceEqual(right!),
        list => list.Aggregate(0, (hash, proposal) => HashCode.Combine(hash, proposal.GetHashCode())),
        list => list.ToList());
}
