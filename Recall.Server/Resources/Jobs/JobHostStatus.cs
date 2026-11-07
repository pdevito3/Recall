namespace Recall.Server.Resources.Jobs;

using BackWave.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Says if the jobs stopped and why, by exception type only. The journal runs on with the jobs stopped.</summary>
public sealed class JobHostStatus(BackWaveHealth health)
{
    private volatile string? _startFault;

    public void ReportStartFault(Exception fault) => _startFault = fault.GetType().FullName;

    public string? StoppedCause()
    {
        var causes = health.HaltedGroups
            .Select(group => $"worker group {group.Key}: {group.Value.ExceptionType}")
            .Concat(_startFault is { } fault ? [$"first use of the job store: {fault}"] : [])
            .ToList();

        return causes.Count == 0 ? null : string.Join("; ", causes);
    }

    public bool IsStopped => StoppedCause() is not null;

    public static string StoppedText(string cause) =>
        $"The background jobs stopped ({cause}). The journal still runs. Fix the cause, then restart the server. No job runs until then. " +
        "See \"The background jobs stopped\" in docs/troubleshooting.md.";
}

/// <summary><see cref="BackWaveHealthCheck"/> says that a group stopped, and this check also says what the person does.</summary>
public sealed class JobHostHealthCheck(JobHostStatus status, BackWaveHealthCheck backWave) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        status.StoppedCause() is { } cause
            ? Task.FromResult(HealthCheckResult.Unhealthy(JobHostStatus.StoppedText(cause)))
            : backWave.CheckHealthAsync(context, cancellationToken);
}
