namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Recordings.Transcription;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Storage;
using Sessions;
using Shouldly;
using TestUtilities;

/// <summary>REC-24: a client that kept the rest of the audio comes back after the sweep ended the upload (REC-23).</summary>
[NotInParallel(nameof(RecordingSweepTests))]
public class LateRecordingPartTests : TestBase
{
    private const long ChunkSamples = 16_000;

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private static FileStore Store => Services.GetRequiredService<FileStore>();

    private static readonly byte[] SecondSlice = RecordingScenario.Bytes(4, 1);

    /// <summary>Gives one word run for each second of audio, from the start sample on, as the real engines do.</summary>
    private sealed class FakeEngine(Func<TranscriptionInput, IAsyncEnumerable<TranscriptChunk>> run) : ITranscriptionEngine
    {
        public List<long> StartSamples { get; } = [];

        public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

        public bool IsReady => true;

        public IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken)
        {
            StartSamples.Add(input.StartSample);
            return run(input);
        }
    }

    private static async IAsyncEnumerable<TranscriptChunk> Chunks(TranscriptionInput input, string[] words, Func<Task>? afterEach = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var total = words.Length * ChunkSamples;
        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = (i + 1) * ChunkSamples;
            if (end > input.StartSample)
            {
                yield return new TranscriptChunk(end, total, words[i]);
                if (afterEach is not null)
                    await afterEach();
            }
        }
    }

    private static FakeEngine Saying(params string[] words) => new(input => Chunks(input, words));

    private static TranscribeRecordingJob JobWith(ITranscriptionEngine engine)
        => new(Services.GetRequiredService<IServiceScopeFactory>(), [engine], Store, Services.GetRequiredService<ModelDownloads>());

    private static Task RunJobAsync(ITranscriptionEngine engine, RecordingDto recording, User owner)
        => JobWith(engine).TranscribeRecordingAsync(recording.Id, owner.Id, owner.TenantId, CancellationToken.None);

    private static Task SweepAsync()
        => new RecordingSweep(Services.GetRequiredService<IServiceScopeFactory>(), Store, Services.GetRequiredService<IJobStore>(), TimeProvider.System)
            .QueueWaitingRecordingsAsync(CancellationToken.None);

    private static Task MakeQuietForADayAsync(string recordingId)
        => JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE recordings SET last_modified_on = $at WHERE id = $id",
            ("$at", Recall.Server.Databases.Sqlite.UtcTextConverter.ToText(DateTimeOffset.UtcNow - Recording.QuietUploadEndsAfter - TimeSpan.FromMinutes(1))),
            ("$id", recordingId));

    /// <summary>An upload with the first two slices that the sweep ended, and the words of those two.</summary>
    private async Task<(RecordingDto Recording, User Owner)> QuietEndedWithWordsAsync(string words)
    {
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 1, SecondSlice)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await MakeQuietForADayAsync(recording.Id);
        await SweepAsync();
        await RunJobAsync(Saying(words), recording, admin);
        var ended = await FactoryClient.GetRecordingAsync(recording.Id);
        ended.Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        return (ended, admin);
    }

    private async Task<string> PlainRawAsync(string sessionId)
    {
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(sessionId));
        return ProseMirrorToPlainText.Render(session!.RawText);
    }

    private static byte[] StoredAudio(RecordingDto recording, User owner)
    {
        using var file = Store.OpenRead(FileKey.ForTenant(owner.TenantId, RecordingFiles.RootFolder, owner.Id, recording.Id, "audio.webm"));
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task late_slices_join_the_audio_in_order_and_only_their_words_follow_the_words_of_the_entry()
    {
        // Arrange
        var (recording, owner) = await QuietEndedWithWordsAsync("first part");
        byte[] third = [3, 3, 3];
        byte[] fourth = [4, 4];

        // Act
        var late = await FactoryClient.PutSliceAsync(recording.Id, 2, third);
        var receiving = await FactoryClient.GetRecordingAsync(recording.Id);
        var repeat = await FactoryClient.PutSliceAsync(recording.Id, 2, third);
        var changed = await FactoryClient.PutSliceAsync(recording.Id, 2, [9]);
        var insideTheJoinedAudio = await FactoryClient.PutSliceAsync(recording.Id, 1, [9]);
        (await FactoryClient.PutSliceAsync(recording.Id, 3, fourth)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var finish = await FactoryClient.FinishAsync(recording.Id, 4, RecordingScenario.WebmStart.Length + SecondSlice.Length + third.Length + fourth.Length);
        var finished = await FactoryClient.GetRecordingAsync(recording.Id);
        await RunJobAsync(Saying("first part", "late part"), recording, owner);

        // Assert
        late.StatusCode.ShouldBe(HttpStatusCode.OK);
        receiving.Status.ShouldBe(nameof(RecordingStatus.Receiving));
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        insideTheJoinedAudio.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        finish.StatusCode.ShouldBe(HttpStatusCode.OK);
        finished.Status.ShouldBe(nameof(RecordingStatus.Received));
        finished.StoredSlices.ShouldBe([0, 1, 2, 3]);
        StoredAudio(recording, owner).ShouldBe([.. RecordingScenario.WebmStart, .. SecondSlice, .. third, .. fourth]);
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first part\nlate part");
        (await FactoryClient.PutSliceAsync(recording.Id, 4, [5])).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task an_edit_of_the_person_between_the_quiet_end_and_the_late_part_stays_and_the_late_words_follow_it()
    {
        // Arrange
        var (recording, owner) = await QuietEndedWithWordsAsync("first part");
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        (await FactoryClient.SaveDraftAsync(recording.SessionId, "first part, as I typed it", session!.RawDraftRevision)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        (await FactoryClient.PutSliceAsync(recording.Id, 2, [3, 3])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.FinishAsync(recording.Id, 3, RecordingScenario.WebmStart.Length + SecondSlice.Length + 2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobAsync(Saying("first part", "late part"), recording, owner);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first part, as I typed it\n\nlate part");
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task a_late_slice_of_a_deleted_entry_gets_404()
    {
        // Arrange
        var (recording, _) = await QuietEndedWithWordsAsync("first part");
        (await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(recording.SessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Act
        var late = await FactoryClient.PutSliceAsync(recording.Id, 2, [3, 3]);

        // Assert
        late.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task a_late_part_that_comes_during_the_run_of_the_first_part_shows_a_true_state_and_gets_its_words_in_that_job()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutSliceAsync(recording.Id, 1, SecondSlice)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await MakeQuietForADayAsync(recording.Id);
        await SweepAsync();
        var states = new List<string>();
        var lateCame = false;
        async Task SendTheLatePartOnce()
        {
            if (lateCame)
                return;
            lateCame = true;
            states.Add((await FactoryClient.GetRecordingAsync(recording.Id)).Status);
            (await FactoryClient.PutSliceAsync(recording.Id, 2, [3, 3])).StatusCode.ShouldBe(HttpStatusCode.OK);
            states.Add((await FactoryClient.GetRecordingAsync(recording.Id)).Status);
            (await FactoryClient.FinishAsync(recording.Id, 3, RecordingScenario.WebmStart.Length + SecondSlice.Length + 2)).StatusCode.ShouldBe(HttpStatusCode.OK);
            states.Add((await FactoryClient.GetRecordingAsync(recording.Id)).Status);
        }

        var engine = new FakeEngine(input => input.StartSample == 0
            ? Chunks(input, ["first part"], SendTheLatePartOnce)
            : Chunks(input, ["first part", "late part"]));

        // Act
        await RunJobAsync(engine, recording, admin);

        // Assert
        states.ShouldBe([nameof(RecordingStatus.Transcribing), nameof(RecordingStatus.Receiving), nameof(RecordingStatus.Received)]);
        engine.StartSamples.ShouldBe([0, ChunkSamples]);
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(session.Id)).ShouldBe("first part\nlate part");
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task an_upload_that_ended_with_no_audio_takes_its_first_slice_and_its_words_come()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(session.Id);
        await MakeQuietForADayAsync(recording.Id);
        await SweepAsync();
        (await FactoryClient.GetRecordingAsync(recording.Id)).Problem.ShouldBe(nameof(RecordingProblem.NoAudioReceived));

        // Act
        var first = await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart);
        var receiving = await FactoryClient.GetRecordingAsync(recording.Id);
        var finish = await FactoryClient.FinishAsync(recording.Id, 1, RecordingScenario.WebmStart.Length);
        await RunJobAsync(Saying("late words"), recording, admin);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        receiving.Status.ShouldBe(nameof(RecordingStatus.Receiving));
        receiving.Problem.ShouldBe(nameof(RecordingProblem.None));
        finish.StatusCode.ShouldBe(HttpStatusCode.OK);
        StoredAudio(recording, admin).ShouldBe(RecordingScenario.WebmStart);
        (await PlainRawAsync(session.Id)).ShouldBe("late words");
    }

    [Test]
    [Property("Rule", "REC-24")]
    public async Task a_late_slice_after_the_audio_was_removed_gets_409_with_the_reason()
    {
        // Arrange
        var (recording, _) = await QuietEndedWithWordsAsync("first part");
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE recordings SET audio_deleted_at = $at WHERE id = $id",
            ("$at", Recall.Server.Databases.Sqlite.UtcTextConverter.ToText(DateTimeOffset.UtcNow)), ("$id", recording.Id));

        // Act
        var late = await FactoryClient.PutSliceAsync(recording.Id, 2, [3, 3]);

        // Assert
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await late.Content.ReadAsStringAsync()).ShouldContain("The server removed the audio of this recording");
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }
}
