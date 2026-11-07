namespace Recall.UnitTests.Domain.Recordings;

using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Recordings.Transcription;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Exceptions;
using Shouldly;

public class RecordingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static Recording Receiving() => Recording.Create("ten_1", "usr_1", "ses_1", "client-1", RecordingFormat.Webm);

    private static Recording Received()
    {
        var recording = Receiving();
        recording.StoreSlice(0, 10);
        recording.Finish([0], 1, 10, TranscriptionEngineKind.Local);
        return recording;
    }

    [Test]
    public void a_new_recording_is_receiving_on_the_local_engine()
    {
        // Arrange and act
        var recording = Receiving();

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Receiving);
        recording.Engine.ShouldBe(TranscriptionEngineKind.Local);
        recording.HighestStoredSlice.ShouldBeNull();
    }

    [Test]
    public void slices_in_any_order_keep_the_highest_index_and_the_sum_of_bytes()
    {
        // Arrange
        var recording = Receiving();

        // Act
        recording.StoreSlice(2, 5);
        recording.StoreSlice(0, 7);

        // Assert
        recording.HighestStoredSlice.ShouldBe(2);
        recording.ByteLength.ShouldBe(12);
    }

    [Test]
    public void a_recording_that_does_not_wait_takes_no_attempt()
    {
        // Arrange
        var receiving = Receiving();
        var failed = Received();
        failed.Fail(RecordingProblem.EngineCrashed);

        // Act and assert
        Should.Throw<ConflictException>(() => receiving.BeginAttempt());
        Should.Throw<ConflictException>(() => failed.BeginAttempt());
    }

    [Test]
    public void a_retry_of_a_recording_that_waits_changes_nothing()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();

        // Act
        recording.Retry(TranscriptionEngineKind.Local);

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Transcribing);
        recording.AttemptsWithoutProgress.ShouldBe(1);
    }

    [Test]
    public void a_retry_after_no_words_reads_the_audio_from_the_start()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(32_000, 32_000, hasWords: false);
        recording.Complete(Now);
        recording.Problem.ShouldBe(RecordingProblem.NoWordsFound);

        // Act
        recording.Retry(TranscriptionEngineKind.Local);

        // Assert
        recording.TranscribedSamples.ShouldBe(0);
    }

    [Test]
    public void a_repeat_of_a_saved_chunk_does_not_advance_the_pointer()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 32_000, hasWords: true);

        // Act
        var advanced = recording.AdvanceTo(16_000, 32_000, hasWords: true);

        // Assert
        advanced.ShouldBeFalse();
    }

    [Test]
    public void transcript_text_keeps_its_characters_so_that_it_takes_no_extra_length()
    {
        // Arrange and act
        var text = RawTranscript.Append("", "don't, käsi");
        var appended = RawTranscript.Append(text, "сказал");

        // Assert
        text.ShouldContain("don't, käsi");
        appended.ShouldContain("сказал");
    }

    [Test]
    public void a_slice_after_the_finish_is_a_conflict()
    {
        // Arrange
        var recording = Received();

        // Act and assert
        Should.Throw<ConflictException>(() => recording.StoreSlice(1, 5));
    }

    [Test]
    public void a_slice_past_the_total_limit_is_a_conflict_and_changes_nothing()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, Recording.MaxBytes);

        // Act and assert
        Should.Throw<ConflictException>(() => recording.StoreSlice(1, 1));
        recording.ByteLength.ShouldBe(Recording.MaxBytes);
        recording.HighestStoredSlice.ShouldBe(0);
    }

    [Test]
    public void a_finish_with_a_gap_stays_receiving()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, 1);
        recording.StoreSlice(2, 1);

        // Act and assert
        Should.Throw<ConflictException>(() => recording.Finish([0, 2], 3, 2, TranscriptionEngineKind.Local));
        recording.Status.ShouldBe(RecordingStatus.Receiving);
        Recording.MissingSlices([0, 2], 3).ShouldBe([1]);
    }

    [Test]
    public void a_finish_with_another_length_stays_receiving()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, 4);

        // Act and assert
        Should.Throw<ConflictException>(() => recording.Finish([0], 1, 5, TranscriptionEngineKind.Local));
        recording.Status.ShouldBe(RecordingStatus.Receiving);
    }

    [Test]
    public void a_finish_that_counts_fewer_slices_than_are_stored_stays_receiving()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, 4);
        recording.StoreSlice(1, 4);

        // Act and assert
        Should.Throw<ConflictException>(() => recording.Finish([0, 1], 1, 8, TranscriptionEngineKind.Local));
    }

    [Test]
    public void the_attempt_after_the_limit_fails_and_says_why()
    {
        // Arrange
        var recording = Received();
        for (var attempt = 0; attempt < Recording.MaxAttemptsWithoutProgress; attempt++)
            recording.BeginAttempt().ShouldBeTrue();

        // Act
        var began = recording.BeginAttempt();

        // Assert
        began.ShouldBeFalse();
        recording.Status.ShouldBe(RecordingStatus.Failed);
        recording.Problem.ShouldBe(RecordingProblem.TooManyAttempts);
    }

    [Test]
    [Property("Rule", "REC-19")]
    public void a_provider_that_never_answers_ends_the_attempts_with_its_own_reason()
    {
        // Arrange
        var recording = Received();
        for (var attempt = 0; attempt < Recording.MaxAttemptsWithoutProgress; attempt++)
        {
            recording.BeginAttempt().ShouldBeTrue();
            recording.WaitForProvider();
        }

        // Act
        var began = recording.BeginAttempt();

        // Assert
        began.ShouldBeFalse();
        recording.Status.ShouldBe(RecordingStatus.Failed);
        recording.Problem.ShouldBe(RecordingProblem.ProviderUnavailable);
    }

    [Test]
    [Property("Rule", "REC-17")]
    public void the_engine_is_fixed_at_the_finish_and_a_retry_of_a_waiting_recording_keeps_it()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, 10);

        // Act
        recording.Finish([0], 1, 10, TranscriptionEngineKind.Cloud);
        recording.Retry(TranscriptionEngineKind.Local);

        // Assert
        recording.Engine.ShouldBe(TranscriptionEngineKind.Cloud);
        recording.Status.ShouldBe(RecordingStatus.Received);
    }

    [Test]
    [Property("Rule", "REC-17")]
    public void a_retry_of_a_failed_recording_takes_the_engine_of_now()
    {
        // Arrange
        var recording = Received();
        recording.Fail(RecordingProblem.ProviderRefused);

        // Act
        recording.Retry(TranscriptionEngineKind.Cloud);

        // Assert
        recording.Engine.ShouldBe(TranscriptionEngineKind.Cloud);
        recording.Status.ShouldBe(RecordingStatus.Received);
        recording.Problem.ShouldBe(RecordingProblem.None);
    }

    [Test]
    [Property("Rule", "REC-21")]
    public void a_hand_over_to_the_local_engine_starts_its_attempts_again()
    {
        // Arrange
        var recording = Receiving();
        recording.StoreSlice(0, 10);
        recording.Finish([0], 1, 10, TranscriptionEngineKind.Cloud);
        recording.BeginAttempt();
        recording.WaitForProvider();

        // Act
        recording.HandToLocalEngine();

        // Assert
        recording.Engine.ShouldBe(TranscriptionEngineKind.Local);
        recording.Status.ShouldBe(RecordingStatus.Received);
        recording.Problem.ShouldBe(RecordingProblem.None);
        recording.AttemptsWithoutProgress.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "REC-7")]
    public void the_limit_of_attempts_ends_before_the_ceiling_of_the_jobs()
    {
        // Arrange
        const int jobCeiling = 10;

        // Act
        var limit = Recording.MaxAttemptsWithoutProgress;

        // Assert
        limit.ShouldBeLessThan(jobCeiling);
    }

    [Test]
    [Property("Rule", "REC-8")]
    public void the_payload_of_a_job_holds_only_ids()
    {
        // Arrange
        Type[] payloads = [typeof(TranscribeRecording), typeof(TranscribeRecordingInCloud), typeof(DeleteRecordingAudio)];

        // Act
        var fields = payloads.SelectMany(p => p.GetProperties()).Select(p => p.Name).Distinct().Order();

        // Assert
        fields.ShouldBe(["RecordingId", "TenantId", "UserId"]);
    }

    [Test]
    public void progress_gives_the_attempts_back()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.BeginAttempt();

        // Act
        recording.AdvanceTo(16_000, 32_000, hasWords: true);

        // Assert
        recording.AttemptsWithoutProgress.ShouldBe(0);
        recording.TranscribedSamples.ShouldBe(16_000);
        recording.DurationMs.ShouldBe(2_000);
    }

    [Test]
    public void a_chunk_that_the_pointer_passed_is_not_new()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 32_000, hasWords: true);

        // Act
        var advanced = recording.AdvanceTo(16_000, 32_000, hasWords: true);

        // Assert
        advanced.ShouldBeFalse();
        recording.TranscribedSamples.ShouldBe(16_000);
    }

    [Test]
    public void a_finished_transcription_keeps_the_audio_for_seven_days()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 16_000, hasWords: true);

        // Act
        recording.Complete(Now);

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Transcribed);
        recording.AudioKeptUntil.ShouldBe(Now.AddDays(7));
        recording.CanDeleteAudio(Now.AddDays(7).AddSeconds(-1)).ShouldBeFalse();
        recording.CanDeleteAudio(Now.AddDays(7)).ShouldBeTrue();
    }

    [Test]
    public void audio_with_no_words_stays_with_a_reason()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 16_000, hasWords: false);

        // Act
        recording.Complete(Now);

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Failed);
        recording.Problem.ShouldBe(RecordingProblem.NoWordsFound);
        recording.AudioKeptUntil.ShouldBeNull();
        recording.CanDeleteAudio(Now.AddYears(1)).ShouldBeFalse();
    }

    [Test]
    public void the_audio_of_a_failed_recording_never_goes_away_by_itself()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();

        // Act
        recording.Fail(RecordingProblem.OutOfMemory);

        // Assert
        recording.CanDeleteAudio(Now.AddYears(10)).ShouldBeFalse();
    }

    [Test]
    public void a_retry_keeps_the_pointer_so_no_text_repeats()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 32_000, hasWords: true);
        recording.Fail(RecordingProblem.EngineCrashed);

        // Act
        recording.Retry(TranscriptionEngineKind.Local);

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Received);
        recording.Problem.ShouldBe(RecordingProblem.None);
        recording.TranscribedSamples.ShouldBe(16_000);
    }

    [Test]
    public void a_retry_of_a_recording_with_its_text_is_a_conflict()
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.AdvanceTo(16_000, 16_000, hasWords: true);
        recording.Complete(Now);

        // Act and assert
        Should.Throw<ConflictException>(() => recording.Retry(TranscriptionEngineKind.Local));
    }

    [Test]
    public void a_recording_that_waits_for_the_model_uses_no_attempt_and_is_still_received()
    {
        // Arrange
        var recording = Received();

        // Act
        recording.WaitForModel();

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Received);
        recording.Problem.ShouldBe(RecordingProblem.WaitingForModel);
        recording.AttemptsWithoutProgress.ShouldBe(0);
    }

    [Test]
    [Arguments(RecordingStatus.Transcribing)]
    public void an_attempt_can_begin_again_after_a_lease_came_back(RecordingStatus status)
    {
        // Arrange
        var recording = Received();
        recording.BeginAttempt();
        recording.Status.ShouldBe(status);

        // Act
        var began = recording.BeginAttempt();

        // Assert
        began.ShouldBeTrue();
        recording.AttemptsWithoutProgress.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public void only_bad_audio_tells_a_person_to_record_again()
    {
        // Arrange
        var problems = Enum.GetValues<RecordingProblem>().Where(p => p is not (RecordingProblem.None or RecordingProblem.AudioUnreadable));

        // Act and assert
        RecordingViews.ProblemTextOf(RecordingProblem.AudioUnreadable, ModelStatus.DownloadFailed).ShouldNotBeNull().ShouldContain("record again");
        foreach (var problem in problems)
            RecordingViews.ProblemTextOf(problem, ModelStatus.DownloadFailed).ShouldNotBeNull().ShouldNotContain("record again", Case.Insensitive, problem.ToString());
    }

    [Test]
    [Property("Rule", "REC-15")]
    public void a_kill_with_a_memory_kill_in_the_same_try_does_not_claim_that_the_memory_kill_hit_the_engine()
    {
        // Act
        var text = RecordingViews.ProblemTextOf(RecordingProblem.OutOfMemory, ModelStatus.DownloadFailed);

        // Assert
        text.ShouldNotBeNull().ShouldContain("during the same try");
        text.ShouldNotContain("needs more memory", Case.Insensitive);
    }

    [Test]
    public void each_problem_tells_a_person_what_happened()
    {
        // Arrange
        var problems = Enum.GetValues<RecordingProblem>().Where(p => p != RecordingProblem.None);

        // Act and assert
        foreach (var problem in problems)
            RecordingViews.ProblemTextOf(problem, ModelStatus.DownloadFailed).ShouldNotBeNullOrWhiteSpace(problem.ToString());
    }

    [Test]
    [Arguments(ModelStatus.DownloadFailed)]
    [Arguments(ModelStatus.HashRejected)]
    public void the_model_text_names_no_retry_time_because_the_owner_sets_it(ModelStatus model)
    {
        // Act
        var text = RecordingViews.ModelTextOf(model);

        // Assert
        text.ShouldNotBeNull();
        text.ShouldContain("tries again");
        text.ShouldNotContain("minutes");
    }

    [Test]
    [Property("Rule", "REC-14")]
    public void a_recording_that_waits_says_that_the_jobs_stopped()
    {
        // Arrange
        var waiting = Received();
        var ended = Received();
        ended.Fail(RecordingProblem.TooLong);

        // Act
        var whileStopped = waiting.ToDto([], ModelStatus.Ready, jobsStopped: true);
        var attempting = Received();
        attempting.BeginAttempt();
        var attemptingWhileStopped = attempting.ToDto([], ModelStatus.Ready, jobsStopped: true);
        var whileRunning = waiting.ToDto([], ModelStatus.Ready, jobsStopped: false);
        var endedWhileStopped = ended.ToDto([], ModelStatus.Ready, jobsStopped: true);

        // Assert
        whileStopped.ProblemText.ShouldBe(GetTranscriptionStatus.JobsStoppedText);
        whileStopped.Status.ShouldBe(nameof(RecordingStatus.Received));
        whileStopped.Problem.ShouldBe(nameof(RecordingProblem.None));
        attemptingWhileStopped.Status.ShouldBe(nameof(RecordingStatus.Transcribing));
        attemptingWhileStopped.ProblemText.ShouldBe(GetTranscriptionStatus.JobsStoppedText);
        whileRunning.ProblemText.ShouldBeNull();
        endedWhileStopped.ProblemText.ShouldBe(RecordingViews.ProblemTextOf(RecordingProblem.TooLong, ModelStatus.Ready));
    }

    [Test]
    public void each_container_has_its_own_first_bytes()
    {
        // Arrange
        byte[] webm = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4];
        byte[] mp4 = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p'];

        // Act and assert
        RecordingFiles.StartsAsFormat(RecordingFormat.Webm, webm).ShouldBeTrue();
        RecordingFiles.StartsAsFormat(RecordingFormat.Mp4, mp4).ShouldBeTrue();
        RecordingFiles.StartsAsFormat(RecordingFormat.Webm, mp4).ShouldBeFalse();
        RecordingFiles.StartsAsFormat(RecordingFormat.Mp4, webm).ShouldBeFalse();
        RecordingFiles.StartsAsFormat(RecordingFormat.Mp4, [0, 0]).ShouldBeFalse();
    }

    [Test]
    public void words_become_a_document_when_raw_is_empty()
    {
        // Arrange and act
        var text = RawTranscript.Append("", "first words");

        // Assert
        ProseMirrorToPlainText.Render(text).ShouldBe("first words");
    }

    [Test]
    public void words_go_after_the_paragraphs_of_a_document()
    {
        // Arrange
        var raw = RawTranscript.Append("", "first words");

        // Act
        var text = RawTranscript.Append(raw, "next words");

        // Assert
        ProseMirrorToPlainText.Render(text).ShouldBe("first words\nnext words");
    }

    [Test]
    public void words_replace_the_one_empty_paragraph_of_a_new_document()
    {
        // Arrange
        const string raw = """{"type":"doc","content":[{"type":"paragraph"}]}""";

        // Act
        var text = RawTranscript.Append(raw, "words");

        // Assert
        text.ShouldBe("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"words"}]}]}""");
    }

    [Test]
    public void words_go_after_plain_text()
    {
        // Arrange and act
        var text = RawTranscript.Append("typed by hand", "heard words");

        // Assert
        text.ShouldBe("typed by hand\n\nheard words");
    }

    [Test]
    [Property("Rule", "REC-14")]
    public void the_status_says_that_the_jobs_stopped_with_what_to_do_and_no_cause()
    {
        // Act
        var status = GetTranscriptionStatus.StatusOf("System.InvalidOperationException", ModelStatus.Ready);

        // Assert
        status.JobsStopped.ShouldBeTrue();
        status.JobsStoppedText.ShouldNotBeNullOrWhiteSpace();
        status.JobsStoppedText.ShouldNotContain("Exception");
    }

    [Test]
    [Property("Rule", "REC-14")]
    public void the_status_names_nothing_while_the_jobs_run()
    {
        // Act
        var status = GetTranscriptionStatus.StatusOf(null, ModelStatus.Ready);

        // Assert
        status.JobsStopped.ShouldBeFalse();
        status.JobsStoppedText.ShouldBeNull();
    }
}
