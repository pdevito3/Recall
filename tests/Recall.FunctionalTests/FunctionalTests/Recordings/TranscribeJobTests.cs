namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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

public class TranscribeJobTests : TestBase
{
    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private sealed class FakeEngine(bool isReady, Func<TranscriptionInput, CancellationToken, IAsyncEnumerable<TranscriptChunk>> run) : ITranscriptionEngine
    {
        public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

        public bool IsReady { get; } = isReady;

        public IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken) => run(input, cancellationToken);
    }

    private static async IAsyncEnumerable<TranscriptChunk> Chunks(TranscriptionInput input, Exception? thenThrow, string[] words, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const long chunkSamples = 16_000;
        var total = words.Length * chunkSamples;
        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = (i + 1) * chunkSamples;
            if (end > input.StartSample)
                yield return new TranscriptChunk(end, total, words[i]);
            await Task.Yield();
        }

        if (thenThrow is not null)
            throw thenThrow;
    }

    private static FakeEngine Saying(params string[] words) => new(true, (input, token) => Chunks(input, null, words, token));

    private static FakeEngine SayingThenFailing(Exception fault, params string[] words) => new(true, (input, token) => Chunks(input, fault, words, token));

    private static TranscribeRecordingJob JobWith(ITranscriptionEngine engine)
        => new(Services.GetRequiredService<IServiceScopeFactory>(), [engine], Services.GetRequiredService<FileStore>(), Services.GetRequiredService<ModelDownloads>());

    private async Task<(RecordingDto Recording, User Admin)> FinishedRecordingAsync()
    {
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        return (await FactoryClient.AddFinishedRecordingAsync(session.Id), admin);
    }

    private async Task<string> PlainRawAsync(string sessionId)
    {
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(sessionId));
        return ProseMirrorToPlainText.Render(session!.RawText);
    }

    private static Task RunAsync(TranscribeRecordingJob job, RecordingDto recording, User owner, CancellationToken cancellationToken = default)
        => job.TranscribeRecordingAsync(recording.Id, owner.Id, owner.TenantId, cancellationToken);

    [Test]
    [Property("Rule", "REC-6")]
    public async Task each_chunk_becomes_text_in_one_revision_and_the_audio_stays_seven_days()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();

        // Act
        await RunAsync(JobWith(Saying("first words", "next words")), recording, admin);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        read.AudioKeptUntil.ShouldNotBeNull();
        read.AudioFileName.ShouldBe("audio.webm");
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words\nnext words");
        var revisions = await FactoryClient.GetFromJsonAsync<System.Text.Json.JsonElement>(ApiRoutes.Sessions.RawRevisions(recording.SessionId));
        var items = revisions.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("number").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task a_job_that_runs_again_after_the_end_repeats_no_text()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(Saying("first words"));
        await RunAsync(job, recording, admin);

        // Act
        await RunAsync(job, recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words");
    }

    [Test]
    public async Task a_lease_that_comes_back_goes_on_after_the_saved_text()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        using var stop = new CancellationTokenSource();
        var job = JobWith(new FakeEngine(true, (input, token) => CancelAfterFirst(input, stop, token)));
        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(job, recording, admin, stop.Token));
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribing));

        // Act
        await RunAsync(JobWith(Saying("first words", "next words")), recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words\nnext words");
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    private static async IAsyncEnumerable<TranscriptChunk> CancelAfterFirst(TranscriptionInput input, CancellationTokenSource stop, [EnumeratorCancellation] CancellationToken token)
    {
        input.StartSample.ShouldBe(0);
        yield return new TranscriptChunk(16_000, 32_000, "first words");
        await stop.CancelAsync();
        token.ThrowIfCancellationRequested();
    }

    [Test]
    [Property("Rule", "REC-5")]
    public async Task an_engine_that_stops_leaves_the_text_so_far_and_the_audio_and_a_reason()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(SayingThenFailing(new TranscriptionEngineException(RecordingProblem.OutOfMemory), "first words"));

        // Act
        await RunAsync(job, recording, admin);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.OutOfMemory));
        read.ProblemText.ShouldNotBeNullOrWhiteSpace();
        read.AudioFileName.ShouldBe("audio.webm");
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words");
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_retry_after_a_failure_goes_on_with_no_repeat()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        await RunAsync(JobWith(SayingThenFailing(new TranscriptionEngineException(RecordingProblem.EngineCrashed), "first words")), recording, admin);

        // Act
        var retry = await FactoryClient.PostAsync(ApiRoutes.Recordings.Retry(recording.Id), null);
        await RunAsync(JobWith(Saying("first words", "next words")), recording, admin);

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words\nnext words");
    }

    [Test]
    [Property("Rule", "REC-7")]
    public async Task a_job_that_keeps_killing_the_process_ends_failed_and_not_stuck()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var killed = new FakeEngine(true, (_, _) => throw new InvalidOperationException("The process died."));

        // Act
        for (var attempt = 0; attempt < Recording.MaxAttemptsWithoutProgress; attempt++)
            await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(JobWith(killed), recording, admin));
        await RunAsync(JobWith(killed), recording, admin);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.TooManyAttempts));
        read.AudioFileName.ShouldBe("audio.webm");
    }

    [Test]
    public async Task a_save_of_the_person_during_a_job_keeps_both_texts()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var client = FactoryClient;
        var session = await client.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        (await client.SaveDraftAsync(recording.SessionId, "typed by hand", session!.RawDraftRevision)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        await RunAsync(JobWith(Saying("heard words")), recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("typed by hand\n\nheard words");
    }

    [Test]
    [Property("Rule", "REC-12")]
    public async Task no_model_stores_the_audio_with_a_true_status_and_no_attempt()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(new FakeEngine(false, (_, _) => throw new InvalidOperationException("An engine that is not ready must not run.")));

        // Act
        await RunAsync(job, recording, admin);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Received));
        read.Problem.ShouldBe(nameof(RecordingProblem.WaitingForModel));
        read.ProblemText.ShouldNotBeNullOrWhiteSpace();
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task audio_that_is_gone_from_the_disk_says_so()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        Services.GetRequiredService<FileStore>().Delete(RecordingFiles.Audio(RecordingScenario.RowOf(recording.Id, admin)));

        // Act
        await RunAsync(JobWith(Saying("words")), recording, admin);

        // Assert
        (await FactoryClient.GetRecordingAsync(recording.Id)).Problem.ShouldBe(nameof(RecordingProblem.AudioMissing));
    }

    [Test]
    public async Task a_recording_deleted_during_the_job_ends_the_job_and_removes_its_late_files()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var store = Services.GetRequiredService<FileStore>();
        var client = FactoryClient;
        var job = JobWith(new FakeEngine(true, (input, token) => DeleteThenSay(client, recording, input, token)));

        // Act
        await RunAsync(job, recording, admin);

        // Assert
        store.FolderNamesUnder(RecordingFiles.OwnerFolder(admin.TenantId, admin.Id)).ShouldNotContain(recording.Id);
        (await PlainRawAsync(recording.SessionId)).ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<TranscriptChunk> DeleteThenSay(HttpClient client, RecordingDto recording, TranscriptionInput input, [EnumeratorCancellation] CancellationToken token)
    {
        (await client.DeleteAsync(ApiRoutes.Recordings.GetRecord(recording.Id), token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.CreateDirectory(Path.GetDirectoryName(input.WorkAudioPath)!);
        await File.WriteAllBytesAsync(input.WorkAudioPath, [1], token);
        yield return new TranscriptChunk(16_000, 16_000, "words");
    }

    [Test]
    public async Task a_job_in_the_scope_of_one_owner_cannot_see_the_journal_of_another()
    {
        // Arrange
        var (recording, _) = await FinishedRecordingAsync();
        var (other, _) = await JournalScenario.AddPeopleAsync();

        // Act
        await RunAsync(JobWith(Saying("words")), recording, other);

        // Assert
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Received));
    }

    [Test]
    [Property("Rule", "REC-6")]
    public async Task a_save_from_before_a_chunk_loses_and_the_chunk_stays_in_the_draft()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        await RunAsync(JobWith(Saying("heard words")), recording, admin);

        // Act
        var save = await FactoryClient.SaveDraftAsync(recording.SessionId, "typed from an old copy", session!.RawDraftRevision, DateTimeOffset.UtcNow.AddSeconds(1));

        // Assert
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PlainRawAsync(recording.SessionId)).ShouldBe("heard words");
    }

    private static async IAsyncEnumerable<TranscriptChunk> WriteWorkAudioThen(IAsyncEnumerable<TranscriptChunk> chunks, TranscriptionInput input)
    {
        await File.WriteAllBytesAsync(input.WorkAudioPath, [1]);
        await File.WriteAllBytesAsync(input.WorkAudioPath + RecordingFiles.PartialSuffix, [1]);
        await foreach (var chunk in chunks)
            yield return chunk;
    }

    private static FakeEngine WritingWorkAudio(FakeEngine engine) => new(true, (input, token) => WriteWorkAudioThen(engine.RunAsync(input, token), input));

    private static FileKey WorkAudioOf(RecordingDto recording, User owner) => RecordingFiles.WorkAudio(RecordingScenario.RowOf(recording.Id, owner));

    private static bool Exists(FileKey key) => Services.GetRequiredService<FileStore>().Exists(key);

    [Test]
    [Property("Rule", "REC-16")]
    public async Task a_job_that_fails_removes_the_decoded_audio_and_keeps_the_audio_and_a_retry_works()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(WritingWorkAudio(SayingThenFailing(new TranscriptionEngineException(RecordingProblem.TooLong), "first words")));

        // Act
        await RunAsync(job, recording, admin);
        var removed = !Exists(WorkAudioOf(recording, admin));
        var partialRemoved = !Exists(RecordingFiles.PartialWorkAudio(RecordingScenario.RowOf(recording.Id, admin)));
        var retry = await FactoryClient.PostAsync(ApiRoutes.Recordings.Retry(recording.Id), null);
        await RunAsync(JobWith(WritingWorkAudio(Saying("first words", "next words"))), recording, admin);

        // Assert
        removed.ShouldBeTrue();
        partialRemoved.ShouldBeTrue();
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(recording.SessionId)).ShouldBe("first words\nnext words");
        Exists(WorkAudioOf(recording, admin)).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "REC-16")]
    public async Task a_job_that_cannot_fit_its_text_removes_the_decoded_audio()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(WritingWorkAudio(Saying(new string('a', Recall.Server.Domain.Sessions.Session.DraftMaxLength + 1))));

        // Act
        await RunAsync(job, recording, admin);

        // Assert
        (await FactoryClient.GetRecordingAsync(recording.Id)).Problem.ShouldBe(nameof(RecordingProblem.TextTooLong));
        Exists(WorkAudioOf(recording, admin)).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "REC-16")]
    public async Task a_job_that_stops_keeps_the_decoded_audio_for_the_next_attempt()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        using var stop = new CancellationTokenSource();
        var job = JobWith(WritingWorkAudio(new FakeEngine(true, (input, token) => CancelAfterFirst(input, stop, token))));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(job, recording, admin, stop.Token));

        // Assert
        Exists(WorkAudioOf(recording, admin)).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "REC-16")]
    public async Task a_job_with_no_model_keeps_the_decoded_audio_for_the_next_attempt()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        using var stop = new CancellationTokenSource();
        var stopping = JobWith(WritingWorkAudio(new FakeEngine(true, (input, token) => CancelAfterFirst(input, stop, token))));
        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(stopping, recording, admin, stop.Token));
        var noModel = JobWith(new FakeEngine(false, (_, _) => throw new InvalidOperationException("An engine that is not ready must not run.")));

        // Act
        await RunAsync(noModel, recording, admin);

        // Assert
        (await FactoryClient.GetRecordingAsync(recording.Id)).Problem.ShouldBe(nameof(RecordingProblem.WaitingForModel));
        Exists(WorkAudioOf(recording, admin)).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_client_that_does_not_read_after_a_lost_save_cannot_push_a_chunk_out_of_the_draft()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        await RunAsync(JobWith(Saying("heard words")), recording, admin);
        var save = await FactoryClient.SaveDraftAsync(recording.SessionId, "typed before the read", session!.RawDraftRevision, DateTimeOffset.UtcNow.AddSeconds(1));
        var lost = await save.Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Act
        var fromTheReply = await FactoryClient.SaveDraftAsync(recording.SessionId, "typed from the reply", lost!.DraftRevision ?? session.RawDraftRevision, DateTimeOffset.UtcNow.AddSeconds(2));
        var fromTheLatest = await FactoryClient.SaveDraftAsync(recording.SessionId, "typed from the latest", lost.LatestRevision, DateTimeOffset.UtcNow.AddSeconds(3));

        // Assert
        lost.Outcome.ShouldBe("Lost");
        lost.DraftRevision.ShouldBeNull();
        (await fromTheReply.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        (await fromTheLatest.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        (await PlainRawAsync(recording.SessionId)).ShouldBe("heard words");
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_client_that_reads_after_a_lost_save_can_save_its_merge()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var session = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        await RunAsync(JobWith(Saying("heard words")), recording, admin);
        await FactoryClient.SaveDraftAsync(recording.SessionId, "typed before the read", session!.RawDraftRevision, DateTimeOffset.UtcNow.AddSeconds(1));

        // Act
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(recording.SessionId));
        var merge = await FactoryClient.SaveDraftAsync(recording.SessionId, "heard words and typed words", read!.RawDraftRevision);

        // Assert
        (await merge.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Won");
        (await PlainRawAsync(recording.SessionId)).ShouldBe("heard words and typed words");
    }

    [Test]
    [Property("Rule", "REC-6")]
    public async Task a_chunk_at_or_before_the_pointer_adds_no_text()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var engine = new FakeEngine(true, (input, token) => TheSameChunkTwice());

        // Act
        await RunAsync(JobWith(engine), recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("heard words");
    }

    private static async IAsyncEnumerable<TranscriptChunk> TheSameChunkTwice()
    {
        yield return new TranscriptChunk(16_000, 16_000, "heard words");
        await Task.Yield();
        yield return new TranscriptChunk(16_000, 16_000, "heard words");
    }

    [Test]
    [Property("Rule", "REC-6")]
    public async Task words_that_cannot_be_stored_fail_the_recording_and_keep_the_pointer()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();

        // Act
        await RunAsync(JobWith(Saying("bad\0words")), recording, admin);

        // Assert
        var read = await FactoryClient.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.EngineCrashed));
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task the_kept_audio_goes_after_seven_days_and_the_text_stays()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingAsync();
        var job = JobWith(Saying("words"));
        await RunAsync(job, recording, admin);

        // Act
        await job.DeleteRecordingAudioAsync(recording.Id, admin.Id, admin.TenantId, CancellationToken.None);

        // Assert
        (await FactoryClient.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PlainRawAsync(recording.SessionId)).ShouldBe("words");
    }

    private sealed record SpokenChunk(int EndMs, string Text, (int StartMs, int EndMs)[] Spans);

    private static readonly SpokenChunk[] Spoken =
    [
        new(4_000, "one two. three four", [(200, 600), (700, 1_100), (2_200, 2_600), (2_700, 3_100)]),
        new(8_000, "five six", [(4_200, 4_600), (4_700, 5_100)]),
        new(12_000, "seven. eight nine", [(8_200, 8_600), (9_800, 10_200), (10_300, 10_700)]),
    ];

    private const int SamplesPerMs = Recording.SamplesPerSecond / 1000;

    private static async IAsyncEnumerable<TranscriptChunk> SpokenChunks(TranscriptionInput input, CancellationTokenSource? stopAfterFirst, [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (var chunk in Spoken)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.EndMs * SamplesPerMs > input.StartSample)
            {
                yield return new TranscriptChunk(chunk.EndMs * SamplesPerMs, Spoken[^1].EndMs * SamplesPerMs, chunk.Text,
                    [.. chunk.Spans.Select(s => new WordTime(s.StartMs * SamplesPerMs, s.EndMs * SamplesPerMs))]);
            }

            if (stopAfterFirst is not null)
            {
                await stopAfterFirst.CancelAsync();
                token.ThrowIfCancellationRequested();
            }

            await Task.Yield();
        }
    }

    private static FakeEngine SayingWithWordTimes() => new(true, (input, token) => SpokenChunks(input, null, token));

    private async Task<(RecordingDto Recording, User Admin)> FinishedRecordingWithTapsAsync(params int[] tapsMs)
    {
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var recording = await FactoryClient.AddRecordingAsync(session.Id);
        (await FactoryClient.PutSliceAsync(recording.Id, 0, RecordingScenario.WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FinishWithTapsAsync(recording.Id, tapsMs)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await FactoryClient.GetRecordingAsync(recording.Id), admin);
    }

    private Task<HttpResponseMessage> FinishWithTapsAsync(string id, int[] tapsMs)
        => FactoryClient.PostAsJsonAsync(ApiRoutes.Recordings.Finish(id), new { sliceCount = 1, byteLength = RecordingScenario.WebmStart.Length, paragraphTapsMs = tapsMs });

    [Test]
    [Property("Rule", "REC-26")]
    [Property("Rule", "REC-27")]
    public async Task two_taps_give_three_paragraphs_with_a_break_between_words_at_each_tap()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingWithTapsAsync(9_000, 1_500);

        // Act
        await RunAsync(JobWith(SayingWithWordTimes()), recording, admin);

        // Assert
        (await FactoryClient.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(recording.SessionId)).ShouldBe("one two.\nthree four five six seven.\neight nine");
    }

    [Test]
    [Property("Rule", "REC-27")]
    public async Task with_no_taps_each_chunk_stays_its_own_paragraph()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingWithTapsAsync();

        // Act
        await RunAsync(JobWith(SayingWithWordTimes()), recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("one two. three four\nfive six\nseven. eight nine");
    }

    [Test]
    [Property("Rule", "REC-28")]
    public async Task a_break_that_waits_at_a_cut_survives_a_lease_return_and_a_run_again_adds_no_break_twice()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingWithTapsAsync(3_900, 9_000);
        using var stop = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(JobWith(new FakeEngine(true, (input, token) => SpokenChunks(input, stop, token))), recording, admin, stop.Token));
        (await PlainRawAsync(recording.SessionId)).ShouldBe("one two. three four");

        // Act
        await RunAsync(JobWith(SayingWithWordTimes()), recording, admin);
        await RunAsync(JobWith(SayingWithWordTimes()), recording, admin);

        // Assert
        (await PlainRawAsync(recording.SessionId)).ShouldBe("one two. three four\nfive six seven.\neight nine");
    }

    [Test]
    [Property("Rule", "REC-25")]
    [Property("Rule", "REC-28")]
    public async Task a_repeat_finish_with_other_taps_gives_the_answer_of_the_repeat_and_keeps_the_first_taps()
    {
        // Arrange
        var (recording, admin) = await FinishedRecordingWithTapsAsync(1_500, 9_000);

        // Act
        var repeat = await FinishWithTapsAsync(recording.Id, [500, 6_000, 11_000]);
        await RunAsync(JobWith(SayingWithWordTimes()), recording, admin);

        // Assert
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        RecordingScenario.RowOf(recording.Id, admin).ParagraphTapsMs.ShouldBe([1_500, 9_000]);
        (await PlainRawAsync(recording.SessionId)).ShouldBe("one two.\nthree four five six seven.\neight nine");
    }
}
