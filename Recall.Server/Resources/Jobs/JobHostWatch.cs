namespace Recall.Server.Resources.Jobs;

using System.Data.Common;
using BackWave;
using BackWave.Storage;
using Serilog;

/// <summary>
/// Enqueues the health job at each start, and writes one log line if the jobs stop. It runs behind the start of the
/// host, because a lock or a fault in the job store must not stop the journal.
/// </summary>
public sealed class JobHostWatch(BackWaveClient client, IJobStore store, JobHostStatus status, TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = Log.ForContext<JobHostWatch>();
    private bool _toldOfWait;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var healthJobWaits = true;
        var told = false;
        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        do
        {
            if (healthJobWaits)
                healthJobWaits = await HealthJobMustTryAgainAsync(stoppingToken);

            if (!told && status.StoppedCause() is { } cause)
            {
                _logger.Here().Fatal("{JobsStopped}", JobHostStatus.StoppedText(cause));
                told = true;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> HealthJobMustTryAgainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await client.EnqueueAsync(new SystemHealth(), dueTime: timeProvider.GetUtcNow(), cancellationToken: stoppingToken);
            return false;
        }
        catch (Exception fault) when (!stoppingToken.IsCancellationRequested)
        {
            if (IsTransient(fault))
            {
                if (!_toldOfWait)
                    _logger.Here().Warning("The health job waits for the job store ({FaultType}), and tries again each {Seconds} s.", fault.GetType().FullName, CheckInterval.TotalSeconds);
                _toldOfWait = true;
                return true;
            }

            status.ReportStartFault(fault);
            return false;
        }
    }

    private bool IsTransient(Exception fault) =>
        fault is DbException { IsTransient: true } or TimeoutException
        || (store is IStoreFaultClassifier classifier && classifier.IsTransientFault(fault));
}
