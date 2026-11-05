namespace Recall.Server.Resources.Storage;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Reports if the root of the <see cref="FileStore"/> takes a write now. A proof writes to the disk and <c>/health</c> has
/// no login, so after a passed proof the next one waits for <see cref="TimeBetweenPassedProofs"/>. A failed proof writes
/// nothing, so each probe tries again and sees a repaired volume at once.
/// </summary>
internal sealed class FileStoreHealthCheck(FileStore fileStore, TimeProvider timeProvider) : IHealthCheck
{
    internal static readonly TimeSpan TimeBetweenPassedProofs = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private DateTimeOffset? _passedAt;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (now - _passedAt < TimeBetweenPassedProofs)
                return Task.FromResult(HealthCheckResult.Healthy());

            var cause = fileStore.ProveWritable();
            _passedAt = cause is null ? now : null;
            return Task.FromResult(cause is null
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, $"The file store cannot take a write. The cause: {cause}"));
        }
    }
}
