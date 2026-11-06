namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Sessions;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class RecordingRaceTests : TestBase
{
    private static readonly byte[] Slice = [0x1A, 0x45, 0xDF, 0xA3, 1];

    private static async Task<(TestingServiceScope UnderTest, TestingServiceScope Rival, Recording Recording)> ScopesWithARecordingAsync(IServiceProvider services)
    {
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "User")
            .BuildAsync();
        var underTest = new TestingServiceScope(services);
        var rival = new TestingServiceScope();
        underTest.SetUserWithDefaults(identifier: owner.Identifier);
        rival.SetUserWithDefaults(identifier: owner.Identifier);

        var session = Session.Create(tenant.Id, owner.Id, $"race-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch);
        var recording = Recording.Create(tenant.Id, owner.Id, session.Id, $"race-{Guid.NewGuid():N}", RecordingFormat.Webm);
        await new TestingServiceScope().InsertAsync(session);
        await new TestingServiceScope().InsertAsync(recording);
        return (underTest, rival, recording);
    }

    private static bool FolderIsGone(TestingServiceScope scope, Recording recording)
        => !scope.GetService<FileStore>().FolderNamesUnder(RecordingFiles.OwnerFolder(recording.TenantId, recording.UserId)).Contains(recording.Id);

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
