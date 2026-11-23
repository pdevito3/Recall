namespace Recall.Server.Domain.Recordings.Jobs;

using BackWave.Storage;
using Databases;
using Features;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Jobs;
using Resources.Storage;
using Serilog;
using Tenants;

public sealed class RecordingSweep(IServiceScopeFactory scopeFactory, FileStore fileStore, IJobStore jobStore, TimeProvider timeProvider)
{
    private readonly ILogger _logger = Log.ForContext<RecordingSweep>();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (owners, deletedTenants, liveTenants) = await ReadOwnersAsync(cancellationToken);

        foreach (var owner in owners)
            await SweepStepAsync(owner.UserId, () => QueueWaitingAsync(owner, cancellationToken));

        foreach (var tenantId in deletedTenants)
            RecordingWork.TryDeleteFolder(fileStore, FileKey.ForTenant(tenantId), tenantId, _logger);

        foreach (var tenantId in liveTenants)
            await SweepStepAsync(tenantId, () => SweepOwnerFoldersOfTenantAsync(tenantId, cancellationToken));

        foreach (var owner in owners)
            await SweepStepAsync(owner.UserId, () => SweepFilesOfOwnerAsync(owner, cancellationToken));

        _logger.Here().Information("Swept the audio of {OwnerCount} owners and {DeletedTenantCount} deleted tenants", owners.Count, deletedTenants.Count);
    }

    private async Task SweepStepAsync(string folderOwnerId, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Here().Warning("Could not sweep the audio of {FolderOwnerId}, because of {FaultType}. A later sweep tries again", folderOwnerId, exception.GetType().Name);
        }
    }

    // Each sweep lists folders before it reads rows. A row commits before its first file, so a new upload is never unknown.
    private async Task SweepOwnerFoldersOfTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        var names = fileStore.FolderNamesUnder(FileKey.ForTenant(tenantId, RecordingFiles.RootFolder)).ToList();
        var users = await ReadUsersOfTenantAsync(tenantId, cancellationToken);
        foreach (var userId in names.Where(name => !users.Contains(name)))
            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.OwnerFolder(tenantId, userId), userId, _logger);
    }

    private async Task<HashSet<string>> ReadUsersOfTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var users = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .TagWithOperationCallSite("recordings.sweep.users_of_tenant")
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return users.ToHashSet(StringComparer.Ordinal);
    }

    public async Task QueueWaitingRecordingsAsync(CancellationToken cancellationToken)
    {
        var (owners, _, _) = await ReadOwnersAsync(cancellationToken);
        foreach (var owner in owners)
            await SweepStepAsync(owner.UserId, () => QueueWaitingAsync(owner, cancellationToken));
    }

    private async Task QueueWaitingAsync(JournalOwner owner, CancellationToken cancellationToken)
    {
        await EndQuietUploadsAsync(owner, cancellationToken);
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var waiting = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.sweep.waiting")
            .AsNoTracking()
            .Where(r => r.Status == RecordingStatus.Received || r.Status == RecordingStatus.Transcribing)
            .Select(r => new { r.Id, r.Engine })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
            return;

        var now = timeProvider.GetUtcNow();
        await dbContext.SaveAndEnqueueAsync(async (jobs, ct) =>
        {
            // One read for the owner, because a read in this callback does not see the jobs that it enqueued.
            var engine = waiting.Any(r => r.Engine == TranscriptionEngineKind.Cloud) ? TranscriptionEngineKind.Cloud : TranscriptionEngineKind.Local;
            var cloudQueue = await RecordingWork.CloudQueueOfAsync(jobStore, owner.TenantId, engine, ct);
            // A job of the local engine waits for the one before it in its queue, but cloud jobs run side by side, and a job that a
            // stop gave back is still in the store. This read comes after the read of the Recordings, so a job that a finish
            // queued with its Recording is seen.
            var inCloud = engine == TranscriptionEngineKind.Cloud
                ? await CleanupQueues.RecordingsInCloudJobsAsync(jobStore, ct)
                : new HashSet<string>();
            foreach (var recording in waiting.Where(r => r.Engine == TranscriptionEngineKind.Local || !inCloud.Contains(r.Id)))
                await RecordingWork.EnqueueAsync(jobs, recording.Id, owner.UserId, owner.TenantId, recording.Engine, cloudQueue, now, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// A Recording that got no new slice for <see cref="Recording.QuietUploadEndsAfter"/> ends with the slices that came, and the
    /// queue below then makes its text. Each stored slice writes the row, so the time of the last write is the time of the last slice.
    /// A slice that comes later opens the upload again (REC-24). A slice that races this save makes the save fail, and the next
    /// sweep looks again. Each Recording ends in its own scope, so one that fails holds no other and not the queue below.
    /// </summary>
    private async Task EndQuietUploadsAsync(JournalOwner owner, CancellationToken cancellationToken)
    {
        var endBefore = timeProvider.GetUtcNow() - Recording.QuietUploadEndsAfter;
        List<string> quiet;
        await using (var scope = scopeFactory.CreateJournalScope(owner))
        {
            // Few Recordings are Receiving at a time, and the time compares here, as SQLite cannot compare a DateTimeOffset.
            var receiving = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Recordings
                .TagWithOperationCallSite("recordings.sweep.receiving")
                .AsNoTracking()
                .Where(r => r.Status == RecordingStatus.Receiving)
                .Select(r => new { r.Id, r.LastModifiedOn })
                .ToListAsync(cancellationToken);
            quiet = receiving.Where(r => r.LastModifiedOn is { } changed && changed <= endBefore).Select(r => r.Id).ToList();
        }

        foreach (var id in quiet)
        {
            try
            {
                await EndQuietUploadAsync(owner, id, endBefore, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Here().Warning("Could not end the quiet upload of recording {RecordingId}, because of {FaultType}. A later sweep tries again", id, exception.GetType().Name);
            }
        }
    }

    private async Task EndQuietUploadAsync(JournalOwner owner, string id, DateTimeOffset endBefore, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recording = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.sweep.quiet")
            .GetById(id, cancellationToken);
        if (recording.Status != RecordingStatus.Receiving || !(recording.LastModifiedOn <= endBefore))
            return;

        var slices = await dbContext.RecordingSlices
            .TagWithOperationCallSite("recordings.sweep.quiet_slices")
            .AsNoTracking()
            .Where(s => s.RecordingId == recording.Id)
            .ToListAsync(cancellationToken);
        var joinedThrough = recording.QuietEndThroughSlice;
        recording.EndQuietUpload(slices.Count, await RecordingWork.ChosenEngineAsync(dbContext, cancellationToken));
        await RecordingWork.JoinAudioAndSaveAsync(dbContext, fileStore, recording, slices, joinedThrough, _logger, cancellationToken);
        _logger.Here().Information("Ended the quiet upload of recording {RecordingId} with {SliceCount} slices", recording.Id, slices.Count);
    }

    private async Task SweepFilesOfOwnerAsync(JournalOwner owner, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = timeProvider.GetUtcNow();
        var folders = fileStore.FolderNamesUnder(RecordingFiles.OwnerFolder(owner.TenantId, owner.UserId)).ToHashSet(StringComparer.Ordinal);
        var rows = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.sweep.rows")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var known = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in folders.Where(name => !known.Contains(name)))
            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(owner.TenantId, owner.UserId, folder), folder, _logger);

        // A Recording whose audio went takes no slice again (REC-24), so a file that a lost write left after the removal goes too.
        foreach (var removed in rows.Where(r => r.AudioDeletedAt is not null && folders.Contains(r.Id)))
            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(removed), removed.Id, _logger);

        foreach (var ended in rows.Where(r => r.Status is RecordingStatus.Transcribed or RecordingStatus.Failed))
            RecordingWork.TryDeleteWorkAudio(fileStore, ended, _logger);

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

public sealed class RecordingSweepService(RecordingSweep sweep, TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan WaitingCheck = TimeSpan.FromHours(1);

    private readonly ILogger _logger = Log.ForContext<RecordingSweepService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunGuardedAsync(sweep.RunAsync, stoppingToken);

        // A queue fault or a dead-lettered job leaves a Recording that waits with no job. A second job for one that has a job does nothing.
        using var timer = new PeriodicTimer(WaitingCheck, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunGuardedAsync(sweep.QueueWaitingRecordingsAsync, stoppingToken);
    }

    private async Task RunGuardedAsync(Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        try
        {
            await work(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.RecordError(System.Diagnostics.Activity.Current, exception);
            _logger.Here().Error("The audio sweep failed with {FaultType}. A later sweep tries again", exception.GetType().Name);
        }
    }
}
