namespace Recall.IntegrationTests.Databases;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class RecordingRaceTests : TestBase
{
    private static readonly byte[] Slice = [0x1A, 0x45, 0xDF, 0xA3, 1];

    internal static async Task<(TestingServiceScope UnderTest, TestingServiceScope Rival, Recording Recording)> ScopesWithARecordingAsync(IServiceProvider services)
    {
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "User")
            .BuildAsync();
        var underTest = new TestingServiceScope(services);
        var rival = new TestingServiceScope();
        underTest.SetUserWithDefaults(identifier: owner.Identifier);
        rival.SetUserWithDefaults(identifier: owner.Identifier);

        var session = Session.Create(tenant.Id, owner.Id, $"race-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, null, TimeZoneId.Utc, DateTimeOffset.UnixEpoch);
        var recording = Recording.Create(tenant.Id, owner.Id, session.Id, $"race-{Guid.NewGuid():N}", RecordingFormat.Webm);
        await new TestingServiceScope().InsertAsync(session);
        await new TestingServiceScope().InsertAsync(recording);
        return (underTest, rival, recording);
    }

    private static bool FolderIsGone(TestingServiceScope scope, Recording recording)
        => !scope.GetService<FileStore>().FolderNamesUnder(RecordingFiles.OwnerFolder(recording.TenantId, recording.UserId)).Contains(recording.Id);

    private static readonly byte[] SecondSlice = [2, 2];

    private static readonly byte[] LateSlice = [3, 3, 3];

    /// <summary>
    /// The first two slices, ended by the sweep after a day with no new slice (REC-23), so a third slice is a late part (REC-24).
    /// The sweep reads each owner, so the tests that use it run one at a time, and each ends only its own Recording.
    /// </summary>
    internal static async Task QuietEndAsync(TestingServiceScope scope, Recording recording, RecordingSweep sweep)
    {
        await scope.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        await scope.SendAsync(new StoreRecordingSlice.Command(recording.Id, 1, SecondSlice));
        await TestFixture.ExecuteOutOfBandAsync(
            "UPDATE recordings SET last_modified_on = @at WHERE id = @id",
            ("@at", UtcTextConverter.ToText(DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1))),
            ("@id", recording.Id));
        await sweep.QueueWaitingRecordingsAsync(CancellationToken.None);
    }

    private static Task<RecordingDto> FinishWithTheLateSliceAsync(TestingServiceScope scope, Recording recording)
        => scope.SendAsync(new FinishRecording.Command(recording.Id, new RecordingFinishDto { SliceCount = 3, ByteLength = Slice.Length + SecondSlice.Length + LateSlice.Length }));

    /// <summary>The audio in the file that the saved row names, as each read of the audio of the server does.</summary>
    internal static byte[] StoredAudio(TestingServiceScope scope, Recording recording)
    {
        var saved = scope.GetService<AppDbContext>().Recordings.AsNoTracking().Single(r => r.Id == recording.Id);
        using var file = scope.GetService<FileStore>().OpenRead(RecordingFiles.Audio(saved));
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel(nameof(QuietEndAsync))]
    public async Task a_late_slice_during_the_start_of_a_run_keeps_its_file_and_joins_the_audio()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.transcribe.begin");
        await using var services = TestFixture.BuildServiceProvider(s => s
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(pause)),
            TestFixture.UnpooledConnectionString);
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);
        await QuietEndAsync(rival, recording, rival.GetService<RecordingSweep>());

        // Act
        var begin = underTest.SendAsync(new BeginTranscription.Command(recording.Id, TranscriptionEngineKind.Local, EngineIsReady: true));
        await pause.ReachedAsync();
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 2, LateSlice));
        pause.Release();
        await begin;
        var finished = await FinishWithTheLateSliceAsync(rival, recording);

        // Assert
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice, .. LateSlice]);
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel(nameof(QuietEndAsync))]
    public async Task the_quiet_end_removes_only_the_files_of_the_slices_that_it_joined()
    {
        // Arrange
        var afterSave = new RivalAfterSave();
        await using var services = TestFixture.BuildServiceProvider(s => s
            .ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(afterSave)),
            TestFixture.UnpooledConnectionString);
        var (_, rival, recording) = await ScopesWithARecordingAsync(services);
        var store = rival.GetService<FileStore>();
        var lateFile = RecordingFiles.Slice(recording, 2, Convert.ToHexStringLower(SHA256.HashData(LateSlice)));
        // The file of a late slice that is on its way, written after the save of the quiet end and before its own save.
        afterSave.Arm(
            context => context.ChangeTracker.Entries<Recording>().Any(e => e.Entity.Id == recording.Id && e.State == EntityState.Modified && e.Entity.QuietEndThroughSlice is not null),
            () => store.WriteAsync(lateFile, new MemoryStream(LateSlice), CancellationToken.None));

        // Act
        await QuietEndAsync(rival, recording, services.GetRequiredService<RecordingSweep>());

        // Assert
        store.Exists(lateFile).ShouldBeTrue();
        Directory.GetFiles(store.LocalPathOf(RecordingFiles.Slices(recording))).Select(Path.GetFileName).ShouldBe([Path.GetFileName(store.LocalPathOf(lateFile))]);
        StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice]);
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel(nameof(QuietEndAsync))]
    public async Task a_late_slice_during_the_removal_of_the_kept_audio_keeps_the_audio()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.audio.remove");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);
        await QuietEndAsync(rival, recording, rival.GetService<RecordingSweep>());
        await TestFixture.ExecuteOutOfBandAsync(
            "UPDATE recordings SET status = 'Transcribed', has_text = 1, audio_kept_until = '2000-01-01T00:00:00.000000Z' WHERE id = @id", ("@id", recording.Id));

        // Act
        var remove = underTest.SendAsync(new RemoveKeptAudio.Command(recording.Id));
        await pause.ReachedAsync();
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 2, LateSlice));
        pause.Release();
        await remove;
        var finished = await FinishWithTheLateSliceAsync(rival, recording);

        // Assert
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice, .. LateSlice]);
    }

    [Test]
    [Property("Rule", "REC-3")]
    public async Task a_finish_that_a_rival_finish_beat_gives_the_answer_of_a_repeat()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.finish.slices");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 1, SecondSlice));
        var dto = new RecordingFinishDto { SliceCount = 2, ByteLength = Slice.Length + SecondSlice.Length };

        // Act
        var finish = underTest.SendAsync(new FinishRecording.Command(recording.Id, dto));
        await pause.ReachedAsync();
        await rival.SendAsync(new FinishRecording.Command(recording.Id, dto));
        pause.Release();
        var answer = await finish;

        // Assert
        answer.Status.ShouldBe(nameof(RecordingStatus.Received));
        StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice]);
    }

    [Test]
    [Property("Rule", "REC-24")]
    [NotInParallel(nameof(QuietEndAsync))]
    public async Task a_late_finish_that_a_rival_late_finish_beat_keeps_the_whole_audio()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.finish.slices");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);
        await QuietEndAsync(rival, recording, rival.GetService<RecordingSweep>());
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 2, LateSlice));

        // Act
        var finish = FinishWithTheLateSliceAsync(underTest, recording);
        await pause.ReachedAsync();
        var beat = await FinishWithTheLateSliceAsync(rival, recording);
        pause.Release();
        var answer = await finish;

        // Assert
        answer.Status.ShouldBe(beat.Status);
        StoredAudio(rival, recording).ShouldBe([.. Slice, .. SecondSlice, .. LateSlice]);
    }

    [Test]
    [Arguments("recordings.slice.existing")]
    [Arguments("recordings.slice.live")]
    [Property("Rule", "REC-11")]
    public async Task a_delete_during_a_slice_write_leaves_no_file_behind(string pauseAfter)
    {
        // Arrange
        var pause = PauseAtQuery.After(pauseAfter);
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);

        // Act
        var store = underTest.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        await pause.ReachedAsync();
        await rival.SendAsync(new DeleteRecording.Command(recording.Id));
        pause.Release();

        // Assert
        await Should.ThrowAsync<NotFoundException>(store);
        FolderIsGone(rival, recording).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task the_database_refuses_a_recording_of_a_deleted_owner()
    {
        // Arrange
        await using var services = PauseAtQuery.After("not.used").BuildServices();
        var (_, _, recording) = await ScopesWithARecordingAsync(services);
        await TestFixture.ExecuteOutOfBandAsync("UPDATE users SET is_deleted = 1 WHERE id = @id", ("@id", recording.UserId));
        var late = Recording.Create(recording.TenantId, recording.UserId, recording.SessionId, $"late-{Guid.NewGuid():N}", RecordingFormat.Webm);

        // Act
        var insert = () => new TestingServiceScope().InsertAsync(late);

        // Assert
        (await Should.ThrowAsync<ForbiddenAccessException>(insert)).Message.ShouldContain(JournalTriggers.OwnerDeletedMessage);
    }

    [Test]
    public async Task two_writes_of_the_same_slice_store_it_once()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.slice.existing");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);

        // Act
        var store = underTest.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        await pause.ReachedAsync();
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        pause.Release();
        var answer = await store;

        // Assert
        answer.ByteLength.ShouldBe(Slice.Length);
        var read = await rival.SendAsync(new GetRecording.Query(recording.Id));
        read.StoredSlices.ShouldBe([0]);
        read.ByteLength.ShouldBe(Slice.Length);
    }

    [Test]
    public async Task the_file_of_a_kept_slice_survives_a_rival_with_other_bytes()
    {
        // Arrange
        var pause = PauseAtQuery.After("recordings.slice.existing");
        await using var services = pause.BuildServices();
        var (underTest, rival, recording) = await ScopesWithARecordingAsync(services);
        byte[] other = [0x1A, 0x45, 0xDF, 0xA3, 2];

        // Act
        var store = underTest.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, other));
        await pause.ReachedAsync();
        await rival.SendAsync(new StoreRecordingSlice.Command(recording.Id, 0, Slice));
        pause.Release();

        // Assert
        await Should.ThrowAsync<ConflictException>(store);
        var saved = await rival.SendAsync(new GetRecording.Query(recording.Id));
        saved.ByteLength.ShouldBe(Slice.Length);
    }
}
