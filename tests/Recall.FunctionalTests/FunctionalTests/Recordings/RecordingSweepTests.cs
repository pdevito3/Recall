namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using System.Net.Http.Json;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelProviders;
using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.EntityGraphs;
using Sessions;
using Shouldly;
using TestUtilities;

[NotInParallel(nameof(RecordingSweepTests))]
public class RecordingSweepTests : TestBase
{
    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private sealed class SweptFolder : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("recall_sweep_").FullName;

        public SweptFolder()
        {
            Store = new FileStore(Options.Create(new FileStoreOptions { Root = _root }));
            Sweep = new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), Store, Services.GetRequiredService<IJobStore>(), TimeProvider.System);
        }

        public FileStore Store { get; }

        public RecordingSweep Sweep { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        public async Task WriteAsync(FileKey key)
        {
            using var content = new MemoryStream([1]);
            await Store.WriteAsync(key, content, CancellationToken.None);
        }

        public bool Holds(FileKey key)
        {
            try
            {
                using var file = Store.OpenRead(key);
                return true;
            }
            catch (NotFoundException)
            {
                return false;
            }
        }
    }

    private static FileStore SharedStore => Services.GetRequiredService<FileStore>();

    public static Task<long> CountJobsAsync(string kind, string idInPayload) => JournalScenario.CountOutOfBandAsync(
        "SELECT count(*) FROM backwave_jobs WHERE wire_name = $kind AND IFNULL(CAST(payload AS TEXT), '') LIKE $id", ("$kind", kind), ("$id", $"%{idInPayload}%"));

    private static async Task<(Tenant Tenant, User Admin)> AddTenantWithAdminAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var user, u => u.Role = UserRole.Admin().Value);
        await InsertAsync(tenant);
        await InsertAsync(user);
        return (tenant, user);
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task the_sweep_removes_the_files_that_a_soft_deleted_tenant_left_and_no_others()
    {
        // Arrange
        var (deleted, admin) = await AddTenantWithAdminAsync();
        var (kept, _) = await AddTenantWithAdminAsync();
        using var folder = new SweptFolder();
        await folder.WriteAsync(FileKey.ForTenant(kept.Id, "exports", "report"));
        FactoryClient.SignInAsAppUser(admin.Identifier);
        (await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(deleted.Id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var lateFile = FileKey.ForTenant(deleted.Id, RecordingFiles.RootFolder, "usr_1", "rec_1", "audio.webm");
        await folder.WriteAsync(lateFile);

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        folder.Holds(lateFile).ShouldBeFalse();
        folder.Holds(FileKey.ForTenant(kept.Id, "exports", "report")).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task the_sweep_removes_a_folder_with_no_recording_and_keeps_one_with_a_recording()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        using var folder = new SweptFolder();
        var orphan = FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, "rec_with_no_row", "work.wav");
        var audio = FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, recording.Id, "audio.webm");
        await folder.WriteAsync(orphan);
        await folder.WriteAsync(audio);

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        folder.Holds(orphan).ShouldBeFalse();
        folder.Holds(audio).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task the_sweep_removes_the_folder_of_a_deleted_user()
    {
        // Arrange
        var (tenant, _) = await AddTenantWithAdminAsync();
        using var folder = new SweptFolder();
        var orphan = FileKey.ForTenant(tenant.Id, RecordingFiles.RootFolder, "usr_that_is_gone", "rec_1", "audio.webm");
        await folder.WriteAsync(orphan);

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        folder.Holds(orphan).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "REC-7")]
    public async Task the_hourly_check_queues_a_recording_that_still_waits()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        using var folder = new SweptFolder();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sweep = new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), folder.Store, Services.GetRequiredService<IJobStore>(), clock);
        using var service = new RecordingSweepService(sweep, clock);
        var before = await CountJobsAsync("transcribe-recording", recording.Id);

        // Act
        await service.StartAsync(CancellationToken.None);
        var afterStart = await WaitForMoreJobsAsync(recording.Id, before, () => { });
        var afterHour = await WaitForMoreJobsAsync(recording.Id, afterStart, () => clock.Advance(RecordingSweepService.WaitingCheck));
        await service.StopAsync(CancellationToken.None);

        // Assert
        afterHour.ShouldBeGreaterThan(afterStart);
    }

    [Test]
    [Property("Rule", "REC-23")]
    public async Task an_upload_with_no_new_slice_for_a_day_ends_with_the_slices_that_came_and_a_late_slice_inside_them_gets_409()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var withGap = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(withGap.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(withGap.Id, 2, RecordingScenario.Bytes(4))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var empty = await FactoryClient.AddRecordingAsync(session.Id);
        var recent = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(recent.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var quietSince = DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1);
        foreach (var id in new[] { withGap.Id, empty.Id })
            await JournalScenario.ExecuteOutOfBandAsync(
                "UPDATE recordings SET last_modified_on = $at WHERE id = $id", ("$at", Recall.Server.Databases.Sqlite.UtcTextConverter.ToText(quietSince)), ("$id", id));
        var sweep = new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), SharedStore, Services.GetRequiredService<IJobStore>(), TimeProvider.System);

        // Act
        await sweep.QueueWaitingRecordingsAsync(CancellationToken.None);

        // Assert
        var ended = await FactoryClient.GetRecordingAsync(withGap.Id);
        ended.Status.ShouldNotBe(nameof(RecordingStatus.Receiving));
        ended.StoredSlices.ShouldBe([0, 2]);
        (await CountJobsAsync("transcribe-recording", withGap.Id)).ShouldBeGreaterThan(0);
        var audio = FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, withGap.Id, "audio.webm");
        using (var file = SharedStore.OpenRead(audio))
        using (var bytes = new MemoryStream())
        {
            await file.CopyToAsync(bytes);
            bytes.ToArray().ShouldBe(RecordingScenario.WebmStart.Concat(RecordingScenario.Bytes(4)).ToArray());
        }

        (await FactoryClient.PutSliceAsync(withGap.Id, 1, RecordingScenario.Bytes(4))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var nothing = await FactoryClient.GetRecordingAsync(empty.Id);
        nothing.Status.ShouldBe(nameof(RecordingStatus.Failed));
        nothing.Problem.ShouldBe(nameof(RecordingProblem.NoAudioReceived));
        nothing.ProblemText.ShouldBe("No audio of this recording reached the server within a day, so the server ended it.");
        (await FactoryClient.PostAsync(ApiRoutes.Recordings.Retry(empty.Id), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await FactoryClient.GetRecordingAsync(recent.Id)).Status.ShouldBe(nameof(RecordingStatus.Receiving));
    }

    [Test]
    [Property("Rule", "REC-23")]
    public async Task a_quiet_upload_with_a_slice_file_that_the_server_lost_ends_with_a_gap_and_says_so()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 1, RecordingScenario.Bytes(4, 1))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 2, RecordingScenario.Bytes(4, 2))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var slices = Path.Combine(SharedStore.LocalPathOf(RecordingFiles.Folder(admin.TenantId, admin.Id, recording.Id)), "slices");
        foreach (var file in Directory.GetFiles(slices, "000001-*"))
            File.Delete(file);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE recordings SET last_modified_on = $at WHERE id = $id",
            ("$at", Recall.Server.Databases.Sqlite.UtcTextConverter.ToText(DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1))),
            ("$id", recording.Id));
        var sweep = new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), SharedStore, Services.GetRequiredService<IJobStore>(), TimeProvider.System);

        // Act
        await sweep.QueueWaitingRecordingsAsync(CancellationToken.None);

        // Assert
        var ended = await FactoryClient.GetRecordingAsync(recording.Id);
        ended.Status.ShouldBe(nameof(RecordingStatus.Received));
        ended.ProblemText.ShouldBe("Part of the audio of this recording was lost on the server, so the words of that part are missing. Tell the Owner of this server.");
        using var audio = SharedStore.OpenRead(FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, recording.Id, "audio.webm"));
        using var bytes = new MemoryStream();
        await audio.CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(RecordingScenario.WebmStart.Concat(RecordingScenario.Bytes(4, 2)).ToArray());
        Directory.GetFiles(slices).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "REC-23")]
    public async Task a_quiet_upload_that_cannot_end_holds_no_other_upload_and_not_the_queue_of_its_owner()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var broken = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(broken.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var quiet = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(quiet.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var jobGone = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await JournalScenario.ExecuteOutOfBandAsync(
            "DELETE FROM backwave_jobs WHERE wire_name = $kind AND CAST(payload AS TEXT) LIKE $id", ("$kind", "transcribe-recording"), ("$id", $"%{jobGone.Id}%"));
        // A folder where the audio goes makes each write of the audio fail.
        Directory.CreateDirectory(Path.Combine(SharedStore.LocalPathOf(RecordingFiles.Folder(admin.TenantId, admin.Id, broken.Id)), "audio.webm"));
        var quietSince = DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1);
        foreach (var id in new[] { broken.Id, quiet.Id })
            await JournalScenario.ExecuteOutOfBandAsync(
                "UPDATE recordings SET last_modified_on = $at WHERE id = $id", ("$at", Recall.Server.Databases.Sqlite.UtcTextConverter.ToText(quietSince)), ("$id", id));
        var sweep = new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), SharedStore, Services.GetRequiredService<IJobStore>(), TimeProvider.System);

        // Act
        await sweep.QueueWaitingRecordingsAsync(CancellationToken.None);

        // Assert
        (await FactoryClient.GetRecordingAsync(broken.Id)).Status.ShouldBe(nameof(RecordingStatus.Receiving));
        (await FactoryClient.GetRecordingAsync(quiet.Id)).Status.ShouldNotBe(nameof(RecordingStatus.Receiving));
        (await CountJobsAsync("transcribe-recording", quiet.Id)).ShouldBeGreaterThan(0);
        (await CountJobsAsync("transcribe-recording", jobGone.Id)).ShouldBeGreaterThan(0);
    }

    // The timer exists only after the first sweep, so one advance can come too early.
    private static async Task<long> WaitForMoreJobsAsync(string recordingId, long known, Action beforeEachLook)
    {
        var count = known;
        for (var tries = 0; tries < 200 && count <= known; tries++)
        {
            beforeEachLook();
            await Task.Delay(100);
            count = await CountJobsAsync("transcribe-recording", recordingId);
        }

        return count;
    }

    [Test]
    [Property("Rule", "REC-7")]
    public async Task the_sweep_queues_each_recording_that_waits_for_text_so_none_stays_stuck()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        using var folder = new SweptFolder();
        var before = await CountJobsAsync("transcribe-recording", recording.Id);

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        (await CountJobsAsync("transcribe-recording", recording.Id)).ShouldBeGreaterThan(before);
    }

    [Test]
    [Property("Rule", "REC-20")]
    public async Task the_sweep_gives_a_cloud_recording_a_job_only_when_it_has_none_so_no_part_goes_out_twice()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, ModelProviderKind.OpenAiCompatible, pickModel: false);
        scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
        (await scenario.Client.PutAsJsonAsync(
            ApiRoutes.ModelProviders.TranscriptionEngine,
            new { modelProviderId = scenario.Provider.Id, model = FakeModelProvider.TranscriptionModelId })).EnsureSuccessStatusCode();
        scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        var session = await scenario.Client.AddSessionAsync();
        var queued = await scenario.Client.AddFinishedRecordingAsync(session.Id);
        var jobGone = await scenario.Client.AddFinishedRecordingAsync(session.Id);
        await JournalScenario.ExecuteOutOfBandAsync(
            "DELETE FROM backwave_jobs WHERE wire_name = $kind AND CAST(payload AS TEXT) LIKE $id",
            ("$kind", TranscribeRecordingJob.CloudWireName), ("$id", $"%{jobGone.Id}%"));
        using var folder = new SweptFolder();

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        (await CountJobsAsync(TranscribeRecordingJob.CloudWireName, queued.Id)).ShouldBe(1);
        (await CountJobsAsync(TranscribeRecordingJob.CloudWireName, jobGone.Id)).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "REC-5")]
    public async Task the_sweep_queues_the_delete_of_audio_past_its_seven_days()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE recordings SET status = 'Transcribed', audio_kept_until = '2000-01-01T00:00:00.000000Z' WHERE id = $id", ("$id", recording.Id));
        using var folder = new SweptFolder();

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        (await CountJobsAsync("delete-recording-audio", recording.Id)).ShouldBeGreaterThan(0);
    }

    [Test]
    [Property("Rule", "REC-16")]
    [Arguments("Failed", false)]
    [Arguments("Transcribed", false)]
    [Arguments("Transcribing", true)]
    public async Task the_sweep_removes_the_decoded_audio_of_an_ended_recording_and_keeps_its_audio(string status, bool stays)
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE recordings SET status = $status WHERE id = $id", ("$status", status), ("$id", recording.Id));
        using var folder = new SweptFolder();
        var workAudio = RecordingFiles.WorkAudio(admin.TenantId, admin.Id, recording.Id);
        var audio = FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, recording.Id, "audio.webm");
        var partial = RecordingFiles.PartialWorkAudio(admin.TenantId, admin.Id, recording.Id);
        await folder.WriteAsync(workAudio);
        await folder.WriteAsync(partial);
        await folder.WriteAsync(audio);

        // Act
        await folder.Sweep.RunAsync(CancellationToken.None);

        // Assert
        folder.Holds(workAudio).ShouldBe(stays);
        folder.Holds(partial).ShouldBe(stays);
        folder.Holds(audio).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "REC-5")]
    public async Task audio_past_its_seven_days_goes_and_the_text_stays()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        (await FactoryClient.SaveDraftAsync(session.Id, "words", 0)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE recordings SET status = 'Transcribed', audio_kept_until = '2000-01-01T00:00:00.000000Z' WHERE id = $id", ("$id", recording.Id));
        var job = Services.GetRequiredService<TranscribeRecordingJob>();

        // Act
        await job.DeleteRecordingAudioAsync(recording.Id, admin.Id, admin.TenantId, CancellationToken.None);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.AudioFileName.ShouldBeNull();
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Should.Throw<NotFoundException>(() => SharedStore.OpenRead(FileKey.ForTenant(admin.TenantId, RecordingFiles.RootFolder, admin.Id, recording.Id, "audio.webm")));
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task a_tenant_delete_that_cannot_remove_the_folder_queues_a_retry_that_removes_it()
    {
        // Arrange
        var (tenant, admin) = await AddTenantWithAdminAsync();
        await AddTenantWithAdminAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"recall_test_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var tenantsFolder = Path.Combine(TestingWebApplicationFactory.Instance.FileRoot, "tenants");
        Directory.CreateDirectory(tenantsFolder);
        var link = Path.Combine(tenantsFolder, tenant.Id);
        Directory.CreateSymbolicLink(link, outside);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        try
        {
            // Act
            var delete = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id));
            var queued = await CountJobsAsync("delete-tenant-files", tenant.Id);
            File.Delete(link);
            var files = FileKey.ForTenant(tenant.Id, RecordingFiles.RootFolder, "usr_1", "rec_1", "audio.webm");
            using (var content = new MemoryStream([1]))
                await SharedStore.WriteAsync(files, content, CancellationToken.None);
            await Services.GetRequiredService<DeleteTenantFilesJob>().DeleteTenantFilesAsync(tenant.Id, CancellationToken.None);

            // Assert
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            queued.ShouldBe(1);
            Should.Throw<NotFoundException>(() => SharedStore.OpenRead(files));
        }
        finally
        {
            if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
                File.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task the_retry_job_keeps_the_files_of_a_tenant_that_is_live()
    {
        // Arrange
        var (tenant, _) = await AddTenantWithAdminAsync();
        var files = FileKey.ForTenant(tenant.Id, RecordingFiles.RootFolder, "usr_1", "rec_1", "audio.webm");
        using (var content = new MemoryStream([1]))
            await SharedStore.WriteAsync(files, content, CancellationToken.None);

        // Act
        await Services.GetRequiredService<DeleteTenantFilesJob>().DeleteTenantFilesAsync(tenant.Id, CancellationToken.None);

        // Assert
        await using var kept = SharedStore.OpenRead(files);
        kept.ReadByte().ShouldBe(1);
    }

    [Test]
    public async Task the_delete_of_a_user_with_recordings_takes_the_rows_and_their_folder()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var delete = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.Delete(member.Id));

        // Assert
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SharedStore.FolderNamesUnder(RecordingFiles.OwnerFolder(member.TenantId, member.Id)).ShouldNotContain(recording.Id);
        (await JournalScenario.CountOutOfBandAsync("SELECT count(*) FROM recordings WHERE id = $id", ("$id", recording.Id))).ShouldBe(0);
    }
}
