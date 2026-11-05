namespace Recall.Server.Auth;

using Resources;
using Serilog;

/// <summary>Removes expired attempts and codes, and sessions that ended 30 days ago. Each hour, in bounded batches.</summary>
public sealed class AuthSweepService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly ILogger Logger = Log.ForContext<AuthSweepService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The next run tries again. An exception here would stop the host.
                    Logger.Here().Error(exception, "The sweep of the sign-in tables failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A normal stop
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        // The sweep has no request, so it starts its own trace.
        using var trace = Telemetry.TraceRoot([], "auth.sweep");
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SessionStore>().SweepAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.RecordError(trace.Activity, exception);
            throw;
        }
    }
}
