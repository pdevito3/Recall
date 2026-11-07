namespace Recall.Server.Resources.Jobs;

using BackWave.Storage;
using Serilog;

/// <summary>
/// A queue limit is a row in the job store. This writes the limits of the Cleanup queues and the Summary queues once, so a
/// start with the limits in place writes nothing.
/// </summary>
public sealed class QueueLimits(IJobStore store, TimeProvider timeProvider) : IHostedService
{
    private const string Actor = "recall-startup";

    private readonly ILogger _logger = Log.ForContext<QueueLimits>();

    public static IReadOnlyList<(string Queue, int Limit)> Wanted { get; } =
    [
        .. CleanupQueues.All.Select(queue => (queue, CleanupQueues.JobsAtOnceInAQueue)),
        .. SummaryQueues.All.Select(queue => (queue, SummaryQueues.JobsAtOnceInAQueue)),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await store.ListQueueSettingsAsync(cancellationToken);
            foreach (var (queue, limit) in Wanted.Where(wanted => !settings.Any(s => s.Queue == wanted.Queue && s.ConcurrencyLimit == wanted.Limit)))
                await store.SetConcurrencyLimitAsync(queue, limit, Actor, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception fault) when (!cancellationToken.IsCancellationRequested)
        {
            // The app still serves its pages, and the next start writes the limits.
            _logger.Here().Warning("The limits of the job queues were not written ({FaultType}).", fault.GetType().FullName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
