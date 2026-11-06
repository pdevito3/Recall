namespace Recall.Server.Resources.Jobs;

using BackWave.Jobs;
using Databases;
using Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

public sealed class SystemHealthJobs(IServiceScopeFactory scopeFactory, IOptions<JobOptions> options)
{
    public const string FailOnPurposeText = "The health job fails on purpose.";

    private readonly ILogger _logger = Log.ForContext<SystemHealthJobs>();

    [Job("system-health", Queue = JobHosting.SystemQueue)]
    [Retry(3, 2, 2)]
    public async Task SystemHealthAsync(CancellationToken cancellationToken)
    {
        var tenantCount = await TenantCountAsync(cancellationToken);

        var settings = options.Value.HealthJob;
        await Task.Delay(TimeSpan.FromSeconds(settings.HoldSeconds), cancellationToken);
        if (settings.FailOnPurpose)
            throw new InvalidOperationException(FailOnPurposeText);

        _logger.Here().Information("The health job can see {TenantCount} tenants", tenantCount);
    }

    public async Task<int> TenantCountAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Tenants
            .TagWithOperationCallSite("jobs.system_health.tenants")
            .CountAsync(cancellationToken);
    }
}
