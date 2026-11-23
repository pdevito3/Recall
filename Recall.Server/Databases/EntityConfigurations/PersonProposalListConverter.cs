namespace Recall.Server.Databases.EntityConfigurations;

using System.Text.Json;
using Domain;
using Domain.Sessions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class PersonProposalListConverter()
    : ValueConverter<IReadOnlyList<PersonProposal>, string>(
        list => JsonSerializer.Serialize(list, Web),
        json => JsonSerializer.Deserialize<List<PersonProposal>>(json, Web) ?? new List<PersonProposal>())
{
    // SQLite does not hold a text to its length, so a list longer than this still saves.
    public const int ColumnMaxLength = 10 * (200 * 6 + 100 + (10 * (EntityId.MaxLength + 3)));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly ValueComparer<IReadOnlyList<PersonProposal>> Comparer = new(
        (left, right) => left!.SequenceEqual(right!),
        list => list.Aggregate(0, (hash, proposal) => HashCode.Combine(hash, proposal.GetHashCode())),
        list => list.ToList());
}
