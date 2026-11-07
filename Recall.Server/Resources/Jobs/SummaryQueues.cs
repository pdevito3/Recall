namespace Recall.Server.Resources.Jobs;

using BackWave.Core;

/// <summary>
/// A Summary waits on a model, as a Cleanup does, and the first open of a Year makes a call for each Day. Each tenant puts its
/// Summary jobs in the queue of its hash, and a queue runs one job at a time. So the jobs of one tenant never call its provider
/// at once, and a tenant with a slow or hung provider holds only its own queue. Two tenants wait on each other only when they
/// have the same hash.
/// </summary>
public static class SummaryQueues
{
    public const string First = "summaries-0";

    public const int Count = 4;

    public const int JobsAtOnceInAQueue = 1;

    public static IReadOnlyList<string> All { get; } = Enumerable.Range(0, Count).Select(NameOf).ToArray();

    public static DispatchPolicy Policy { get; } = new DispatchPolicy.Weighted(All.Select(queue => (queue, 1)).ToArray());

    public static string Of(string tenantId) => NameOf(StableHash.BucketOf(tenantId, Count));

    private static string NameOf(int index) => $"summaries-{index}";
}
