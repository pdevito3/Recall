namespace Recall.Server.Resources.Jobs;

/// <summary>A hash of a string that each process gives the same, so a key keeps its queue across a restart.</summary>
public static class StableHash
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>The bucket of <paramref name="key"/> in 0 to <paramref name="count"/> - 1.</summary>
    public static int BucketOf(string key, int count)
    {
        // The hash of a string differs in each process.
        var hash = FnvOffset;
        foreach (var c in key)
            hash = (hash ^ c) * FnvPrime;
        return (int)(hash % (uint)count);
    }
}
