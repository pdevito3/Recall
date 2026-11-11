namespace Recall.IntegrationTests.Databases;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

/// <summary>Each join writes a new audio file, and only the save that names it makes it the audio (REC-24).</summary>
public class RecordingAudioFileTests : TestBase
{
    private static readonly byte[] Slice = [0x1A, 0x45, 0xDF, 0xA3, 1];

    private static readonly byte[] SecondSlice = [2, 2];

    private static readonly byte[] LateSlice = [3, 3, 3];

    private static string[] AudioFiles(FileStore store, Recording recording)
        => Directory.GetFiles(store.LocalPathOf(RecordingFiles.Folder(recording)), "audio*");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The awaited step did not happen within 30 s.");
            await Task.Delay(20);
        }
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel("QuietEndAsync")]
    public async Task a_quiet_end_that_loses_to_a_finish_leaves_the_audio_of_the_finish()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.sweep.quiet_slices");
        var holds = new HoldBeforeSave();
        await using var services = TestFixture.BuildServiceProvider(s => s
            .ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(pause, holds)),
            TestFixture.UnpooledConnectionString);
        var (underTest, rival, recording) = await RecordingRaceTests.ScopesWithARecordingAsync(services);
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 1, SecondSlice));
        await TestFixture.ExecuteOutOfBandAsync(
            "UPDATE recordings SET last_modified_on = @at WHERE id = @id",
            ("@at", UtcTextConverter.ToText(DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1))),
            ("@id", recording.Id));
        var store = rival.GetService<FileStore>();
        byte[] sweepAudio = [.. Slice, .. SecondSlice];
        // The finish holds before its save until the stale sweep wrote its audio of the first two slices.
        var finishSaved = holds.Arm(
            context => Changes(context, recording, r => r.Status == RecordingStatus.Received && r.QuietEndThroughSlice is null),
            async () =>
            {
                pause.Release();
                await WaitUntilAsync(() => AudioFiles(store, recording).Any(file => File.ReadAllBytes(file).SequenceEqual(sweepAudio)));
            });
        // The sweep holds before its save until the finish saved, so the sweep loses.
        _ = holds.Arm(context => Changes(context, recording, r => r.QuietEndThroughSlice is not null), () => finishSaved);

        // Act
        var sweep = services.GetRequiredService<RecordingSweep>().QueueWaitingRecordingsAsync(CancellationToken.None);
        await pause.ReachedAsync();
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 2, LateSlice));
        var finished = await underTest.SendAsync(new FinishRecording.Command(recording.Id,
            new RecordingFinishDto { SliceCount = 3, ByteLength = Slice.Length + SecondSlice.Length + LateSlice.Length }));
        await sweep;

        // Assert
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        finished.ProblemText.ShouldBeNull();
        RecordingRaceTests.StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice, .. LateSlice]);
        AudioFiles(store, recording).ShouldHaveSingleItem();
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel("QuietEndAsync")]
    public async Task a_late_finish_that_loses_its_save_deletes_only_its_own_audio_file_and_joins_again()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.finish.slices");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await RecordingRaceTests.ScopesWithARecordingAsync(services);
        await RecordingRaceTests.QuietEndAsync(rival, recording, rival.GetService<RecordingSweep>());
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 2, LateSlice));
        var store = rival.GetService<FileStore>();

        // Act
        var finish = underTest.SendAsync(new FinishRecording.Command(recording.Id,
            new RecordingFinishDto { SliceCount = 3, ByteLength = Slice.Length + SecondSlice.Length + LateSlice.Length }));
        await pause.ReachedAsync();
        // A rival save that joins nothing, so the first save of the finish loses on the version.
        await TestFixture.ExecuteOutOfBandAsync("UPDATE recordings SET version = version + 1 WHERE id = @id", ("@id", recording.Id));
        pause.Release();
        var finished = await finish;

        // Assert
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        finished.ProblemText.ShouldBeNull();
        RecordingRaceTests.StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice, .. LateSlice]);
        AudioFiles(store, recording).ShouldHaveSingleItem();
    }

    private static bool Changes(DbContext context, Recording recording, Func<Recording, bool> matches)
        => context.ChangeTracker.Entries<Recording>().Any(e => e.Entity.Id == recording.Id && e.State == EntityState.Modified && matches(e.Entity));
}

/// <summary>
/// Holds each armed save once, at its start and before it writes, and tells when that save wrote. The save holds the writer
/// line while it waits, so a hold must not wait for a save of another context.
/// </summary>
internal sealed class HoldBeforeSave : SaveChangesInterceptor
{
    private readonly Lock _lock = new();
    private readonly List<(Func<DbContext, bool> Matches, Func<Task> Hold, TaskCompletionSource Saved)> _armed = [];
    private readonly ConcurrentDictionary<DbContext, TaskCompletionSource> _held = new();

    /// <returns>A task that ends when the held save wrote.</returns>
    public Task Arm(Func<DbContext, bool> matches, Func<Task> hold)
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _armed.Add((matches, hold, saved));
        return saved.Task;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context)
            return result;
        (Func<DbContext, bool> Matches, Func<Task> Hold, TaskCompletionSource Saved)? arm;
        lock (_lock)
        {
            var index = _armed.FindIndex(a => a.Matches(context));
            arm = index < 0 ? null : _armed[index];
            if (index >= 0)
                _armed.RemoveAt(index);
        }

        if (arm is { } held)
        {
            _held[context] = held.Saved;
            await held.Hold();
        }

        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _held.TryRemove(context, out var saved))
            saved.TrySetResult();
        return ValueTask.FromResult(result);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _held.TryRemove(context, out var saved))
            saved.TrySetException(eventData.Exception);
        return Task.CompletedTask;
    }
}
