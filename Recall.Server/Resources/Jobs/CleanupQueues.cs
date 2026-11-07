namespace Recall.Server.Resources.Jobs;

using BackWave.Core;

/// <summary>
/// A Cleanup waits on a model, and a model can hang for minutes, so its jobs have their own worker group. Each queue has a limit
/// on the jobs that run at once, and a tenant uses one queue at a time, so a tenant with a hung provider holds only the slots of
/// its own queue. <see cref="Pick"/> gives a tenant a queue that no other tenant uses, so two tenants share a queue only when
/// more than <see cref="Count"/> tenants have Cleanups that have not ended. Then the workers are as full as the queues.
/// </summary>
public static class CleanupQueues
{
    public const string First = "cleanup-0";

    public const int Count = 8;

    public const int JobsAtOnceInAQueue = 3;

    /// <summary>Each queue can fill its limit at one time, so a queue at its limit takes no worker from another queue.</summary>
    public const int Workers = Count * JobsAtOnceInAQueue;

    public static IReadOnlyList<string> All { get; } = Enumerable.Range(0, Count).Select(NameOf).ToArray();

    public static DispatchPolicy Policy { get; } = new DispatchPolicy.Weighted(All.Select(queue => (queue, 1)).ToArray());

    /// <summary>
    /// The queue of a new Cleanup of <paramref name="tenantId"/>, from the queue and the tenant of each Cleanup that has not
    /// ended. A tenant stays in the queue that it uses, so its limit holds. A tenant with none takes the queue with the fewest
    /// such Cleanups, its own hash queue first on a tie. So it takes a queue that no tenant uses while one is free, and when each
    /// queue is in use, a queue with a free slot before a queue that a hung tenant fills.
    /// </summary>
    public static string Pick(string tenantId, IReadOnlyCollection<(string Queue, string TenantId)> unfinished)
    {
        if (unfinished.FirstOrDefault(job => job.TenantId == tenantId).Queue is { } own)
            return own;

        var load = unfinished.CountBy(job => job.Queue).ToDictionary(StringComparer.Ordinal);
        var hashed = Of(tenantId);
        return All.OrderBy(queue => load.GetValueOrDefault(queue)).ThenBy(queue => queue != hashed).First();
    }

    public static string Of(string tenantId) => NameOf(StableHash.BucketOf(tenantId, Count));

    private static string NameOf(int index) => $"cleanup-{index}";
}
