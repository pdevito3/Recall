namespace Recall.Server.Resources.Storage;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

public sealed class FileStoreHealthCheck(Func<string?> proveWritable, string root, TimeProvider timeProvider) : IHealthCheck
{
    public static readonly TimeSpan TimeBetweenProofs = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongestWaitForAProof = TimeSpan.FromSeconds(5);

    private static ILogger Logger => Log.ForContext<FileStoreHealthCheck>();

    private readonly Lock _gate = new();
    private Task<string?>? _proof;
    private long _proofStartedAt;
    private bool _proofHangLogged;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var cause = await CurrentProof().WaitAsync(LongestWaitForAProof, timeProvider, cancellationToken);
            return cause is null
                ? HealthCheckResult.Healthy()
                : new(context.Registration.FailureStatus, $"The file store cannot take a write. The cause: {cause}");
        }
        catch (TimeoutException)
        {
            LogTheHangOnce();
            return new(context.Registration.FailureStatus, "The file store did not finish a write in time.");
        }
    }

    private Task<string?> CurrentProof()
    {
        lock (_gate)
        {
            if (_proof is null || (_proof.IsCompleted && timeProvider.GetElapsedTime(_proofStartedAt) >= TimeBetweenProofs))
            {
                _proof = Task.Run(proveWritable);
                _proofStartedAt = timeProvider.GetTimestamp();
                _proofHangLogged = false;
            }

            return _proof;
        }
    }

    private void LogTheHangOnce()
    {
        lock (_gate)
        {
            if (_proofHangLogged)
                return;

            _proofHangLogged = true;
        }

        Logger.Here().Error("The file store root {Root} did not finish a write in {Seconds} seconds, so its disk can be hanging", root, LongestWaitForAProof.TotalSeconds);
    }
}
