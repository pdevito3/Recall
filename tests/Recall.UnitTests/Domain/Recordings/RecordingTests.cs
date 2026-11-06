namespace Recall.UnitTests.Domain.Recordings;

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
        recording.Finish([0], 1, 10);
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
        Should.Throw<ConflictException>(() => recording.Finish([0, 2], 3, 2));
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
        Should.Throw<ConflictException>(() => recording.Finish([0], 1, 5));
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
        Should.Throw<ConflictException>(() => recording.Finish([0, 1], 1, 8));
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
        Type[] payloads = [typeof(TranscribeRecording), typeof(DeleteRecordingAudio)];

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
        recording.Retry();

        // Assert
        recording.Status.ShouldBe(RecordingStatus.Received);
        recording.Problem.ShouldBe(RecordingProblem.None);
        recording.TranscribedSamples.ShouldBe(16_000);
    }

    [Test]
    public void a_retry_of_a_recording_that_did_not_fail_is_a_conflict()
    {
        // Arrange
        var recording = Received();

        // Act and assert
        Should.Throw<ConflictException>(recording.Retry);
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
    public void each_problem_tells_a_person_what_happened()
    {
        // Arrange
        var problems = Enum.GetValues<RecordingProblem>().Where(p => p != RecordingProblem.None);

        // Act and assert
        foreach (var problem in problems)
            RecordingViews.ProblemTextOf(problem, ModelStatus.DownloadFailed).ShouldNotBeNullOrWhiteSpace(problem.ToString());
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
}
