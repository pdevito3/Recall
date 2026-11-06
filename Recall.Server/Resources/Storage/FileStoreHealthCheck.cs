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

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problem = await WriteProblemAsync(cancellationToken);
        return problem is null ? HealthCheckResult.Healthy() : new(context.Registration.FailureStatus, problem);
    }

    /// <summary>Null means that the file store takes writes. The text names a path of the host, so it stays in the log.</summary>
    public async Task<string?> WriteProblemAsync(CancellationToken cancellationToken)
    {
        var (proof, timeLeft) = CurrentProof();
        try
        {
            var cause = await proof.WaitAsync(timeLeft, timeProvider, cancellationToken);
            return cause is null ? null : $"The file store cannot take a write. The cause: {cause}";
        }
        catch (TimeoutException)
        {
            return "The file store did not finish a write in time.";
        }
    }

    private (Task<string?> Proof, TimeSpan TimeLeft) CurrentProof()
    {
        lock (_gate)
        {
            if (_proof is null || (_proof.IsCompleted && timeProvider.GetElapsedTime(_proofStartedAt) >= TimeBetweenProofs))
            {
                _proof = Task.Run(proveWritable);
                _proofStartedAt = timeProvider.GetTimestamp();
                _ = LogIfItHangsAsync(_proof);
            }

            var timeLeft = LongestWaitForAProof - timeProvider.GetElapsedTime(_proofStartedAt);
            return (_proof, timeLeft > TimeSpan.Zero ? timeLeft : TimeSpan.Zero);
        }
    }

    private async Task LogIfItHangsAsync(Task proof)
    {
        await Task.WhenAny(proof, Task.Delay(LongestWaitForAProof, timeProvider));
        if (!proof.IsCompleted)
            Logger.Here().Error("The file store root {Root} did not finish a write in {Seconds} seconds, so its disk can be hanging", root, LongestWaitForAProof.TotalSeconds);
    }
}
