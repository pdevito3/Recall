namespace Recall.Server.Domain.ImportBatches;

public readonly record struct ImportSlot(DateOnly Date, int Position);

public static class ImportPositions
{
    public static IReadOnlyDictionary<string, string> Check(
        IReadOnlyDictionary<string, ImportSlot> stored, IReadOnlyDictionary<string, ImportSlot> proposed)
    {
        var after = new Dictionary<string, ImportSlot>(stored);
        foreach (var (id, slot) in proposed)
            after[id] = slot;

        var takenBy = new Dictionary<string, string>();
        while (Clashes(after, stored, proposed, takenBy) is { Count: > 0 } refused)
        {
            foreach (var (id, other) in refused)
            {
                takenBy[id] = other;
                KeepStoredSlot(after, stored, id);
            }
        }

        return takenBy;
    }

    private static List<(string Id, string Other)> Clashes(
        Dictionary<string, ImportSlot> after, IReadOnlyDictionary<string, ImportSlot> stored, IReadOnlyDictionary<string, ImportSlot> proposed,
        Dictionary<string, string> takenBy)
        => after.GroupBy(entry => entry.Value, entry => entry.Key)
            .Where(holders => holders.Count() > 1)
            .SelectMany(holders =>
            {
                var stays = holders.FirstOrDefault(id => stored.TryGetValue(id, out var slot) && slot == holders.Key);
                return holders
                    .Where(id => id != stays && proposed.ContainsKey(id) && !takenBy.ContainsKey(id))
                    .Select(id => (Id: id, Other: stays ?? holders.Where(other => other != id).Order(StringComparer.Ordinal).First()));
            })
            .ToList();

    private static void KeepStoredSlot(Dictionary<string, ImportSlot> after, IReadOnlyDictionary<string, ImportSlot> stored, string id)
    {
        if (stored.TryGetValue(id, out var slot))
            after[id] = slot;
        else
            after.Remove(id);
    }
}
