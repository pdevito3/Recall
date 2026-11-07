namespace Recall.Server.Domain.Summaries.Jobs;

using Databases;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Resources.Jobs;
using Resources;
using Serilog;
using Tenants;
using UserSettings;

/// <summary>
/// At each start, puts the nightly schedule of each live User in place in the time zone of the User, and removes the schedule
/// of a User that is gone. A change of the time zone moves the schedule at once, and an open of a period puts it in place for a
/// User who joined after the start.
/// </summary>
public sealed class SummaryScheduleSync(IServiceScopeFactory scopeFactory, SummarySchedules schedules) : BackgroundService
{
    private readonly ILogger _logger = Log.ForContext<SummaryScheduleSync>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await SyncAsync(stoppingToken);
        }
        catch (Exception fault) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.Here().Warning("The nightly summary schedules were not put in place ({FaultType}). An open of a period puts the schedule of its User in place.", fault.GetType().FullName);
        }
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        List<JournalOwner> owners;
        await using (var scope = scopeFactory.CreateSystemScope())
        {
            owners = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
                .TagWithOperationCallSite("summaries.schedule_sync.users")
                .AsNoTracking()
                .Select(u => new JournalOwner(u.Id, u.TenantId))
                .ToListAsync(cancellationToken);
        }

        foreach (var owner in owners)
        {
            await using var scope = scopeFactory.CreateJournalScope(owner);
            var zone = await scope.ServiceProvider.GetRequiredService<AppDbContext>().LoadJournalZoneAsync(cancellationToken);
            await schedules.TryEnsureAsync(owner.UserId, owner.TenantId, zone, cancellationToken);
        }

        await schedules.RemoveAllButAsync(owners.Select(o => o.UserId).ToHashSet(), cancellationToken);
        _logger.Here().Information("Put the nightly summary schedules of {UserCount} users in place", owners.Count);
    }
}
