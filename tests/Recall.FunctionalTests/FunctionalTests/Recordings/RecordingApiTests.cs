namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Resources.Storage;
using Sessions;
using Shouldly;
using TestUtilities;

public class RecordingApiTests : TestBase
{
    private static FileStore Store => TestingWebApplicationFactory.Instance.Services.GetRequiredService<FileStore>();

    private async Task<(string SessionId, Recall.Server.Domain.Users.User Admin, Recall.Server.Domain.Users.User Member)> SignedInWithSessionAsync()
    {
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        return (session.Id, admin, member);
    }

    private static byte[] StoredAudio(RecordingDto recording, string tenantId, string userId)
    {
        using var file = Store.OpenRead(FileKey.ForTenant(tenantId, RecordingFiles.RootFolder, userId, recording.Id, recording.AudioFileName!));
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    [Test]
    [Property("Rule", "REC-1")]
    public async Task a_replay_of_the_create_gives_the_same_recording()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var first = await FactoryClient.AddRecordingAsync(sessionId, clientRecordingId: "device-1");

        // Act
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Recordings.OfSession(sessionId), new { clientRecordingId = "device-1", format = "Webm" });

        // Assert
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<RecordingDto>())!.Id.ShouldBe(first.Id);
    }

    [Test]
    public async Task an_unknown_format_is_refused()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();

        // Act
        var response = await FactoryClient.PostAsJsonAsync(ApiRoutes.Recordings.OfSession(sessionId), new { clientRecordingId = "x", format = "Exe" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    [Property("Rule", "REC-2")]
    public async Task a_slice_repeated_and_out_of_order_stores_the_bytes_of_the_source()
    {
        // Arrange
        var (sessionId, admin, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);
        var second = RecordingScenario.Bytes(5, 2);

        // Act
        (await FactoryClient.PutSliceAsync(recording.Id, 1, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 1, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var finish = await FactoryClient.FinishAsync(recording.Id, 2, RecordingScenario.WebmStart.Length + second.Length);

        // Assert
        finish.StatusCode.ShouldBe(HttpStatusCode.OK);
        var finished = await FactoryClient.GetRecordingAsync(recording.Id);
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        finished.HighestStoredSlice.ShouldBe(1);
        finished.ByteLength.ShouldBe(RecordingScenario.WebmStart.Length + second.Length);
        StoredAudio(finished, admin.TenantId, admin.Id).ShouldBe([.. RecordingScenario.WebmStart, .. second]);
    }

    [Test]
    public async Task a_stored_slice_does_not_change()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);
        await FactoryClient.PutSliceAsync(recording.Id, 1, RecordingScenario.Bytes(5, 2));

        // Act
        var other = await FactoryClient.PutSliceAsync(recording.Id, 1, RecordingScenario.Bytes(5, 3));

        // Assert
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FactoryClient.GetRecordingAsync(recording.Id)).ByteLength.ShouldBe(5);
    }

    [Test]
    [Property("Rule", "REC-3")]
    public async Task a_finish_with_a_gap_is_a_conflict_and_the_stored_slices_show_what_to_send()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);
        await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart);
        await FactoryClient.PutSliceAsync(recording.Id, 2, RecordingScenario.Bytes(3));

        // Act
        var finish = await FactoryClient.FinishAsync(recording.Id, 3, RecordingScenario.WebmStart.Length + 3);

        // Assert
        finish.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Receiving));
        read.StoredSlices.ShouldBe([0, 2]);
    }

    [Test]
    public async Task a_second_finish_gives_the_same_answer()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);

        // Act
        var again = await FactoryClient.FinishAsync(recording.Id, 2, recording.ByteLength);

        // Assert
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Property("Rule", "REC-7")]
    public async Task a_second_finish_and_a_retry_queue_the_work_again_while_it_waits()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);
        var before = await RecordingSweepTests.CountJobsAsync("transcribe-recording", recording.Id);

        // Act
        var finish = await FactoryClient.FinishAsync(recording.Id, 2, recording.ByteLength);
        var retry = await FactoryClient.PostAsync(ApiRoutes.Recordings.Retry(recording.Id), null);

        // Assert
        finish.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RecordingSweepTests.CountJobsAsync("transcribe-recording", recording.Id)).ShouldBeGreaterThanOrEqualTo(before + 2);
    }

    [Test]
    public async Task a_first_slice_that_is_not_the_named_format_is_refused()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.PutSliceAsync(recording.Id, 0, "MZ this is a program"u8.ToArray());

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await FactoryClient.GetRecordingAsync(recording.Id)).StoredSlices.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "REC-4")]
    public async Task a_slice_over_the_limit_is_refused_with_a_problem()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.PutSliceAsync(recording.Id, 1, RecordingScenario.Bytes(Recording.MaxSliceBytes + 1));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await FactoryClient.GetRecordingAsync(recording.Id)).StoredSlices.ShouldBeEmpty();
    }

    [Test]
    public async Task a_slice_at_the_limit_is_stored()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.PutSliceAsync(recording.Id, 1, RecordingScenario.Bytes(Recording.MaxSliceBytes));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_slice_of_another_media_type_is_refused()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart, "application/json");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task an_empty_slice_is_refused()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.PutSliceAsync(recording.Id, 1, []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task the_owner_downloads_the_audio_with_its_type_and_a_range()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Recordings.File(recording.Id, "audio.webm"));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);

        // Act
        var response = await FactoryClient.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("audio/webm");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(RecordingScenario.WebmStart[..4]);
    }

    [Test]
    public async Task a_download_with_no_session_is_401()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);
        using var anonymous = TestingWebApplicationFactory.Instance.CreateClient();

        // Act
        var response = await anonymous.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Property("Rule", "REC-10")]
    public async Task a_download_of_the_file_of_another_user_is_404()
    {
        // Arrange
        var (sessionId, _, member) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var file = await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"));
        var row = await FactoryClient.GetAsync(ApiRoutes.Recordings.GetRecord(recording.Id));
        var slice = await FactoryClient.PutSliceAsync(recording.Id, 2, RecordingScenario.Bytes(1));

        // Assert
        file.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        row.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        slice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("..%2Faudio.webm")]
    [Arguments("..%2F..%2F..%2Fappsettings.json")]
    [Arguments("work.wav")]
    [Arguments("audio.mp4")]
    public async Task a_name_other_than_the_audio_file_is_refused(string name)
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.GetAsync($"{ApiRoutes.Recordings.GetRecord(recording.Id)}/files/{name}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task a_download_while_the_upload_runs_is_404()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(sessionId);

        // Act
        var response = await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task the_delete_of_a_session_takes_its_recordings_and_their_files()
    {
        // Arrange
        var (sessionId, admin, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);
        var folder = RecordingFiles.Folder(admin.TenantId, admin.Id, recording.Id);

        // Act
        var delete = await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(sessionId));

        // Assert
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.GetRecord(recording.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Store.FolderNamesUnder(RecordingFiles.OwnerFolder(admin.TenantId, admin.Id)).ShouldNotContain(recording.Id);
        Should.Throw<Recall.Server.Exceptions.NotFoundException>(() => Store.OpenRead(FileKey.Of($"{folder}/audio.webm")));
    }

    [Test]
    public async Task the_delete_of_a_recording_takes_its_folder_and_leaves_the_session()
    {
        // Arrange
        var (sessionId, admin, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);

        // Act
        var delete = await FactoryClient.DeleteAsync(ApiRoutes.Recordings.GetRecord(recording.Id));

        // Assert
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Store.FolderNamesUnder(RecordingFiles.OwnerFolder(admin.TenantId, admin.Id)).ShouldNotContain(recording.Id);
        (await FactoryClient.GetAsync(ApiRoutes.Sessions.GetRecord(sessionId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task the_recordings_of_a_session_are_listed_for_its_owner()
    {
        // Arrange
        var (sessionId, _, _) = await SignedInWithSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(sessionId);

        // Act
        var list = await FactoryClient.GetFromJsonAsync<RecordingDto[]>(ApiRoutes.Recordings.OfSession(sessionId));

        // Assert
        list!.ShouldHaveSingleItem().Id.ShouldBe(recording.Id);
    }

    [Test]
    public async Task a_recording_needs_a_session_of_the_caller()
    {
        // Arrange
        var (sessionId, _, member) = await SignedInWithSessionAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var response = await FactoryClient.PostAsJsonAsync(ApiRoutes.Recordings.OfSession(sessionId), new { clientRecordingId = "x", format = "Webm" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("Rule", "REC-14")]
    public async Task the_status_says_that_the_jobs_run_and_names_no_fault()
    {
        // Arrange
        await SignedInWithSessionAsync();

        // Act
        var status = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Recordings.Status);

        // Assert
        status.GetProperty("jobsStopped").GetBoolean().ShouldBeFalse();
        status.GetProperty("jobsStoppedText").ValueKind.ShouldBe(JsonValueKind.Null);
        GetTranscriptionStatus.JobsStoppedText.ShouldNotContain("Exception");
    }
}
