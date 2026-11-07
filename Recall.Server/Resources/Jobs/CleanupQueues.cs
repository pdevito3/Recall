namespace Recall.Server.Resources.Jobs;

using BackWave.Core;
using BackWave.Storage;
using Serilog;

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

    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

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

    public static string Of(string tenantId)
    {
        // The hash of a string differs in each process.
        var hash = FnvOffset;
        foreach (var c in tenantId)
            hash = (hash ^ c) * FnvPrime;
        return NameOf((int)(hash % Count));
    }

    private static string NameOf(int index) => $"cleanup-{index}";
}

/// <summary>A queue limit is a row in the job store. This writes it once, so a start with the limits in place writes nothing.</summary>
public sealed class CleanupQueueLimits(IJobStore store, TimeProvider timeProvider) : IHostedService
{
    private const string Actor = "recall-startup";

    private readonly ILogger _logger = Log.ForContext<CleanupQueueLimits>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await store.ListQueueSettingsAsync(cancellationToken);
            foreach (var queue in CleanupQueues.All.Where(queue => !settings.Any(s => s.Queue == queue && s.ConcurrencyLimit == CleanupQueues.JobsAtOnceInAQueue)))
                await store.SetConcurrencyLimitAsync(queue, CleanupQueues.JobsAtOnceInAQueue, Actor, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception fault) when (!cancellationToken.IsCancellationRequested)
        {
            // The app still serves its pages, and the next start writes the limits.
            _logger.Here().Warning("The limits of the Cleanup queues were not written ({FaultType}).", fault.GetType().FullName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
