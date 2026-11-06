namespace Recall.Server.Resources.Jobs;

using BackWave;
using BackWave.Jobs;
using Databases;
using Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Serilog;

public sealed class SystemHealthJobs(IServiceScopeFactory scopeFactory, JobOptions options)
{
    private readonly ILogger _logger = Log.ForContext<SystemHealthJobs>();

    [Job("system-health", Queue = JobHosting.SystemQueue)]
    [Retry(3, 2, 2)]
    public async Task SystemHealthAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantCount = await dbContext.Tenants
            .TagWithOperationCallSite("jobs.system_health.tenants")
            .CountAsync(cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(options.HealthJob.HoldSeconds), cancellationToken);
        if (options.HealthJob.FailOnPurpose)
            throw new InvalidOperationException("The health job fails on purpose, because Jobs:HealthJob:FailOnPurpose is true.");

        _logger.Here().Information("The health job can see {TenantCount} tenants", tenantCount);
    }
}

/// <summary>Enqueues one health job at each start, so an idle install keeps no schedule that writes to the disk.</summary>
public sealed class HealthJobOnStart(BackWaveClient client, TimeProvider timeProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await client.EnqueueAsync(new SystemHealth(), dueTime: timeProvider.GetUtcNow(), cancellationToken: cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
