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
        while (true)
        {
            var refused = after.GroupBy(entry => entry.Value, entry => entry.Key)
                .Where(holders => holders.Count() > 1)
                .SelectMany(holders => holders
                    .Where(id => proposed.ContainsKey(id) && !takenBy.ContainsKey(id))
                    .Select(id => (Id: id, Other: holders.Where(other => other != id).Order(StringComparer.Ordinal).First())))
                .ToList();
            if (refused.Count == 0)
                return takenBy;

            foreach (var (id, other) in refused)
            {
                takenBy[id] = other;
                if (stored.TryGetValue(id, out var slot))
                    after[id] = slot;
                else
                    after.Remove(id);
            }
        }
    }
}
