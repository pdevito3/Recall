namespace Recall.Server.Domain.ImportBatches;

/// <summary>The date and the position of an entry in its batch.</summary>
public readonly record struct ImportSlot(DateOnly Date, int Position);

public static class ImportPositions
{
    /// <summary>
    /// The entries of a call that would share a date and a position with another entry after the call (IMP-8), each with
    /// the ID of the other entry. The check reads the state after the whole call, so a call can swap two positions. A
    /// refused entry keeps its stored slot, and that can refuse another entry, so the check repeats until it refuses none.
    /// </summary>
    /// <param name="stored">The slot of each stored entry that the call can meet: each entry of the call, and each entry on a date of the call.</param>
    /// <param name="proposed">The slot that the call gives each of its entries that no earlier outcome refused.</param>
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
