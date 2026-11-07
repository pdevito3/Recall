namespace Recall.Server.Resources.Jobs;

using BackWave.Core;
using BackWave.Storage;
using Serilog;

/// <summary>
/// A Cleanup waits on a model, and a model can hang for minutes, so its jobs have their own worker group. Each tenant maps to
/// one queue, with a limit on the jobs that run at once in the queue, so a tenant with a hung provider holds only the slots of
/// its queue. Two tenants can map to one queue, and then they share its slots.
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

    public static string Of(string tenantId)
    {
        // The hash of a string differs in each process, and a retry must stay on its queue.
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
