namespace Recall.Server.Domain.Recordings.Jobs;

using Databases;
using Features;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Storage;
using Serilog;
using Tenants;

/// <summary>
/// Puts the files and the jobs back in line with the rows. It runs after each start, and it is the safety net of each
/// path that can end early: a crash between a commit and a folder delete, a failed delete, a job in the dead letters, or a
/// start with the jobs off. A system scope sees no journal, so it reads each journal in a scope of its owner.
/// </summary>
public sealed class RecordingSweep(IServiceScopeFactory scopeFactory, FileStore fileStore, TimeProvider timeProvider)
{
    private readonly ILogger _logger = Log.ForContext<RecordingSweep>();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (owners, deletedTenants, liveTenants) = await ReadOwnersAsync(cancellationToken);

        foreach (var tenantId in deletedTenants)
            RecordingWork.TryDeleteFolder(fileStore, FileKey.ForTenant(tenantId), tenantId, _logger);

        foreach (var tenantId in liveTenants)
        {
            var folder = FileKey.ForTenant(tenantId, RecordingFiles.RootFolder);
            foreach (var userId in fileStore.FolderNamesUnder(folder).Where(name => !owners.Contains(new JournalOwner(name, tenantId))))
                RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.OwnerFolder(tenantId, userId), userId, _logger);
        }

        foreach (var owner in owners)
        {
            await QueueWaitingAsync(owner, cancellationToken);
            await SweepFilesOfOwnerAsync(owner, cancellationToken);
        }

        _logger.Here().Information("Swept the audio of {OwnerCount} owners and {DeletedTenantCount} deleted tenants", owners.Count, deletedTenants.Count);
    }

    /// <summary>Queues each Recording that waits for text. A job for a Recording that has its text ends at once.</summary>
    public async Task QueueWaitingRecordingsAsync(CancellationToken cancellationToken)
    {
        var (owners, _, _) = await ReadOwnersAsync(cancellationToken);
        foreach (var owner in owners)
            await QueueWaitingAsync(owner, cancellationToken);
    }

    private async Task QueueWaitingAsync(JournalOwner owner, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var waiting = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.sweep.waiting")
            .AsNoTracking()
            .Where(r => r.Status == RecordingStatus.Received || r.Status == RecordingStatus.Transcribing)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
            return;

        var now = timeProvider.GetUtcNow();
        await dbContext.SaveAndEnqueueAsync(async (jobs, ct) =>
        {
            foreach (var id in waiting)
                await jobs.EnqueueAsync(new TranscribeRecording(id, owner.UserId, owner.TenantId), now, ct);
        }, cancellationToken);
    }

    private async Task SweepFilesOfOwnerAsync(JournalOwner owner, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = timeProvider.GetUtcNow();
        var rows = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.sweep.rows")
            .AsNoTracking()
            .Select(r => new { r.Id, r.Status, r.AudioKeptUntil, r.AudioDeletedAt })
            .ToListAsync(cancellationToken);

        var known = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in fileStore.FolderNamesUnder(RecordingFiles.OwnerFolder(owner.TenantId, owner.UserId)).Where(name => !known.Contains(name)))
            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(owner.TenantId, owner.UserId, folder), folder, _logger);

        var overdue = rows
            .Where(r => r.Status == RecordingStatus.Transcribed && r.AudioDeletedAt is null && r.AudioKeptUntil <= now)
            .Select(r => r.Id)
            .ToList();
        if (overdue.Count == 0)
            return;

        await dbContext.SaveAndEnqueueAsync(async (jobs, ct) =>
        {
            foreach (var id in overdue)
                await jobs.EnqueueAsync(new DeleteRecordingAudio(id, owner.UserId, owner.TenantId), now, ct);
        }, cancellationToken);
    }

    private async Task<(HashSet<JournalOwner> Owners, List<string> DeletedTenants, List<string> LiveTenants)> ReadOwnersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await dbContext.Users
            .TagWithOperationCallSite("recordings.sweep.users")
            .AsNoTracking()
            .Select(u => new { u.Id, u.TenantId })
            .ToListAsync(cancellationToken);
        var tenants = await dbContext.Tenants
            .TagWithOperationCallSite("recordings.sweep.tenants")
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(t => new { t.Id, t.IsDeleted })
            .ToListAsync(cancellationToken);

        return (
            users.Select(u => new JournalOwner(u.Id, u.TenantId)).ToHashSet(),
            tenants.Where(t => t.IsDeleted).Select(t => t.Id).ToList(),
            tenants.Where(t => !t.IsDeleted).Select(t => t.Id).ToList());
    }
}

/// <summary>Runs the sweep behind the start, so a large folder cannot hold the boot. It runs only on a host with workers.</summary>
public sealed class RecordingSweepService(RecordingSweep sweep) : BackgroundService
{
    private readonly ILogger _logger = Log.ForContext<RecordingSweepService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await sweep.RunAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.RecordError(System.Diagnostics.Activity.Current, exception);
            _logger.Here().Error("The audio sweep failed with {FaultType}. The next start tries again", exception.GetType().Name);
        }
    }
}
