namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Exceptions;
using Shouldly;

public class SessionTests
{
    private const string PersonId = "per_01";

    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Noon.AddDays(1);

    private static Session NewSession() => Session.Create("ten_1", "usr_1", "device-1", Noon, null, Now);

    private static DraftSave Save(string text, int baseRevision, DateTimeOffset? savedAt = null)
        => DraftSave.Of(text, baseRevision, savedAt ?? Noon, Now);

    private static string Mention(string label)
        => "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Lunch with \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + PersonId + "\",\"label\":\"" + label + "\"}}]}]}";

    [Test]
    public void only_a_save_from_a_base_older_than_the_last_chunk_misses_the_transcript()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save("typed", 0));
        session.TryAppendTranscript("heard", Now).ShouldBeTrue();
        var chunk = session.Raw.DraftRevision;

        // Act
        var old = session.SaveRaw(Save("old copy", chunk - 1, Now.AddHours(1)));
        var current = session.SaveRaw(Save("merged", chunk, Now.AddHours(2)));

        // Assert
        old.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        current.Outcome.ShouldBe(DraftSaveOutcome.Won);
        session.Raw.Text.ShouldBe("merged");
    }

    private static Session CleanSession()
    {
        var session = NewSession();
        session.SaveRaw(Save("raw words", 0));
        session.BeginCleanup();
        session.CompleteCleanup("clean words", "A recap", session.Raw.DraftRevision, Noon);
        return session;
    }

    private static Session SessionAt(CleanupRunState state)
    {
        var session = NewSession();
        session.SaveRaw(Save("raw words", 0));
        if (state == CleanupRunState.NotRun)
            return session;

        session.BeginCleanup();
        if (state == CleanupRunState.Clean)
            session.CompleteCleanup("clean words", "A recap", session.Raw.DraftRevision, Noon);
        else
            session.FailCleanup();
        return session;
    }

    [Test]
    [Property("Rule", "SES-2")]
    public void a_start_time_in_the_future_becomes_the_time_of_the_server()
    {
        // Act
        var session = Session.Create("ten_1", "usr_1", "device-1", Now.AddYears(1), null, Now);

        // Assert
        session.StartedAt.ShouldBe(Now);
    }

    [Test]
    [Property("Rule", "SES-2")]
    public void a_start_time_with_an_offset_is_the_same_instant()
    {
        // Act
        var session = Session.Create("ten_1", "usr_1", "device-1", new DateTimeOffset(2026, 6, 1, 15, 0, 0, TimeSpan.FromHours(3)), null, Now);

        // Assert
        session.StartedAt.ShouldBe(Noon);
    }

    [Test]
    [Property("Rule", "SES-4")]
    public void a_session_with_no_cleanup_has_the_status_not_run_after_a_save()
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save("words", 0));

        // Assert
        session.CleanupStatus.ShouldBe(CleanupStatus.NotRun);
    }

    [Test]
    [Property("Rule", "SES-4")]
    public void a_won_save_of_raw_after_a_cleanup_makes_the_session_stale()
    {
        // Arrange
        var session = CleanSession();

        // Act
        session.SaveRaw(Save("newer words", session.Raw.DraftRevision));

        // Assert
        session.CleanupStatus.ShouldBe(CleanupStatus.Stale);
    }

    [Test]
    [Property("Rule", "SES-4")]
    public void a_lost_save_of_raw_does_not_make_the_session_stale()
    {
        // Arrange
        var session = CleanSession();

        // Act
        var outcome = session.SaveRaw(Save("older words", 0, Noon.AddHours(-1)));

        // Assert
        outcome.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        session.CleanupStatus.ShouldBe(CleanupStatus.Clean);
    }

    [Test]
    [Property("Rule", "SES-4")]
    public void a_hand_edit_of_cleaned_does_not_make_the_session_stale()
    {
        // Arrange
        var session = CleanSession();

        // Act
        session.SaveCleaned(Save("edited by hand", session.Cleaned.DraftRevision));

        // Assert
        session.CleanupStatus.ShouldBe(CleanupStatus.Clean);
        session.CleanedHasHandEdits.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-30")]
    public void a_cleanup_that_runs_refuses_a_second_begin()
    {
        // Arrange
        var session = NewSession();
        session.BeginCleanup();

        // Act
        var begin = () => session.BeginCleanup();

        // Assert
        begin.ShouldThrow<ValidationException>();
        session.CleanupStatus.ShouldBe(CleanupStatus.Running);
    }

    [Test]
    [Property("Rule", "SES-30")]
    [Arguments(CleanupRunState.NotRun)]
    [Arguments(CleanupRunState.Clean)]
    [Arguments(CleanupRunState.Failed)]
    public void a_cleanup_that_does_not_run_refuses_a_complete_and_keeps_the_words(CleanupRunState state)
    {
        // Arrange
        var session = SessionAt(state);
        var cleaned = session.Cleaned;

        // Act
        var complete = () => session.CompleteCleanup("late words", "A late recap", session.Raw.DraftRevision, Now);

        // Assert
        complete.ShouldThrow<ValidationException>();
        session.Cleaned.ShouldBe(cleaned);
        session.CleanupRun.ShouldBe(state);
    }

    [Test]
    [Property("Rule", "SES-30")]
    [Arguments(CleanupRunState.NotRun)]
    [Arguments(CleanupRunState.Clean)]
    [Arguments(CleanupRunState.Failed)]
    public void a_cleanup_that_does_not_run_refuses_a_failure(CleanupRunState state)
    {
        // Arrange
        var session = SessionAt(state);

        // Act
        var fail = () => session.FailCleanup();

        // Assert
        fail.ShouldThrow<ValidationException>();
        session.CleanupRun.ShouldBe(state);
    }

    [Test]
    [Property("Rule", "SES-30")]
    [Arguments(CleanupRunState.NotRun)]
    [Arguments(CleanupRunState.Clean)]
    [Arguments(CleanupRunState.Failed)]
    public void a_cleanup_can_begin_from_each_state_with_no_run(CleanupRunState state)
    {
        // Arrange
        var session = SessionAt(state);

        // Act
        session.BeginCleanup();

        // Assert
        session.CleanupRun.ShouldBe(CleanupRunState.Running);
    }

    [Test]
    [Property("Rule", "SES-4")]
    public void a_run_in_progress_shows_as_running_and_a_failure_shows_as_failed()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save("words", 0));

        // Act
        session.BeginCleanup();
        var whileRunning = session.CleanupStatus;
        session.FailCleanup();

        // Assert
        whileRunning.ShouldBe(CleanupStatus.Running);
        session.CleanupStatus.ShouldBe(CleanupStatus.Failed);
    }

    [Test]
    [Property("Rule", "SES-6")]
    public void the_words_of_a_lost_save_stay_as_the_newest_revision()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save("first", 0, Noon));

        // Act
        session.SaveRaw(Save("older words", 0, Noon.AddHours(-1)));

        // Assert
        session.Raw.Text.ShouldBe("first");
        session.Raw.LatestRevision.ShouldBe(2);
        session.Preview.ShouldBe("first");
    }

    [Test]
    [Property("Rule", "SES-9")]
    public void a_rename_changes_each_mention_and_moves_each_text_to_a_new_revision()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));
        session.SaveCleaned(Save(Mention("Sam"), 0));

        // Act
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        session.Raw.Text.ShouldContain("Samuel");
        session.Raw.DraftRevision.ShouldBe(2);
        session.Cleaned.Text.ShouldContain("Samuel");
        session.Cleaned.DraftRevision.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-9")]
    public void a_rename_of_a_person_with_no_mention_makes_no_revision()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));

        // Act
        session.RenamePerson("per_other", "Someone", Noon.AddHours(1));

        // Assert
        session.Raw.DraftRevision.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-9")]
    public void a_rename_keeps_each_character_of_the_other_words()
    {
        // Arrange
        var text = Mention("Sam").Replace("Lunch with ", "Söögiaeg \U0001F600 with ", StringComparison.Ordinal);
        var session = NewSession();
        session.SaveRaw(Save(text, 0));

        // Act
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        ProseMirrorToPlainText.Render(session.Raw.Text).ShouldBe("Söögiaeg \U0001F600 with Samuel");
    }

    [Test]
    [Property("Rule", "SES-3")]
    public void a_session_stores_its_start_as_utc_at_the_precision_of_the_database()
    {
        // Arrange
        var claimed = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.FromHours(5)).AddTicks(1234567);

        // Act
        var session = Session.Create("ten_1", "usr_1", "device-1", claimed, null, Noon);

        // Assert
        session.StartedAt.Offset.ShouldBe(TimeSpan.Zero);
        session.StartedAt.ShouldBe(new DateTimeOffset(2026, 5, 1, 5, 0, 0, TimeSpan.Zero).AddTicks(1234560));
    }

    [Test]
    [Property("Rule", "SES-10")]
    public void a_rename_on_a_session_that_is_not_stale_moves_the_revision_that_the_last_cleanup_read()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));
        session.BeginCleanup();
        session.CompleteCleanup(Mention("Sam"), "Lunch with Sam", session.Raw.DraftRevision, Noon);

        // Act
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        session.LastCleanupRawRevision.ShouldBe(session.Raw.DraftRevision);
        session.CleanupStatus.ShouldBe(CleanupStatus.Clean);
    }

    [Test]
    [Property("Rule", "SES-10")]
    public void a_rename_on_a_stale_session_keeps_it_stale()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));
        session.BeginCleanup();
        session.CompleteCleanup(Mention("Sam"), "Lunch with Sam", session.Raw.DraftRevision, Noon);
        session.SaveRaw(Save(Mention("Sam") + " ", session.Raw.DraftRevision));
        var readByCleanup = session.LastCleanupRawRevision;

        // Act
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        session.LastCleanupRawRevision.ShouldBe(readByCleanup);
        session.CleanupStatus.ShouldBe(CleanupStatus.Stale);
    }

    [Test]
    [Property("Rule", "SES-10")]
    public void a_rename_does_not_change_the_synopsis()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));
        session.BeginCleanup();
        session.CompleteCleanup(Mention("Sam"), "Lunch with Sam", session.Raw.DraftRevision, Noon);

        // Act
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        session.Synopsis.ShouldBe("Lunch with Sam");
    }

    [Test]
    [Property("Rule", "SES-20")]
    public void cleanup_does_not_change_raw_and_clears_the_mark_of_a_hand_edit()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save("raw words", 0));
        session.SaveCleaned(Save("edited by hand", 0));
        var raw = session.Raw;

        // Act
        session.BeginCleanup();
        session.CompleteCleanup("clean words", "A recap", raw.DraftRevision, Noon);

        // Assert
        session.Raw.ShouldBe(raw);
        session.Cleaned.Text.ShouldBe("clean words");
        session.Cleaned.DraftRevision.ShouldBe(2);
        session.CleanedHasHandEdits.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void metadata_is_trimmed_in_one_spelling_and_has_no_duplicate()
    {
        // Act
        var metadata = SessionMetadata.Of("  walking ", [" joyful", "JOYFUL", "my own mood", "  "], [" work ", "Work", "trips"]);

        // Assert
        metadata.Activity.ShouldBe("Walking");
        metadata.Moods.ShouldBe(["Joyful", "my own mood"]);
        metadata.UserTopics.ShouldBe(["work", "trips"]);
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void a_blank_activity_is_none_and_a_list_with_too_many_values_is_refused()
    {
        // Arrange
        var tooMany = Enumerable.Range(0, Session.MetadataListMaxCount + 1).Select(i => $"topic {i}");

        // Act
        var blank = SessionMetadata.Of(" ", [], []);
        var refused = () => SessionMetadata.Of(null, [], tooMany);

        // Assert
        blank.Activity.ShouldBe(SessionActivity.None);
        refused.ShouldThrow<ValidationException>().Property.ShouldBe("userTopics");
    }

    [Test]
    [Property("Rule", "SES-22")]
    public void the_people_of_a_session_are_the_persons_that_raw_and_cleaned_mention()
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save(Mention("Sam"), 0));
        var whileMentioned = session.MentionedPersonIds.ToList();
        session.SaveRaw(Save("no mention now", 1));

        // Assert
        whileMentioned.ShouldBe([PersonId]);
        session.MentionedPersonIds.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-22")]
    public void a_person_that_only_one_text_mentions_is_still_a_person_of_the_session()
    {
        // Arrange
        var session = NewSession();
        session.SaveRaw(Save(Mention("Sam"), 0));
        session.SaveCleaned(Save(Mention("Sam"), 0));

        // Act
        session.SaveRaw(Save("no mention now", 1));

        // Assert
        session.MentionedPersonIds.ShouldBe([PersonId]);
    }

    [Test]
    [Property("Rule", "SES-23")]
    public void the_preview_has_the_first_characters_of_the_plain_text_and_follows_a_rename()
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save(Mention("Sam"), 0));
        var saved = session.Preview;
        session.RenamePerson(PersonId, "Samuel", Noon.AddHours(1));

        // Assert
        saved.ShouldBe("Lunch with Sam");
        session.Preview.ShouldBe("Lunch with Samuel");
    }

    [Test]
    [Property("Rule", "SES-23")]
    public void the_preview_stops_at_its_limit_and_does_not_cut_a_character_in_two()
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save(string.Concat(Enumerable.Repeat("\U0001F600", Session.PreviewMaxLength + 5)), 0));

        // Assert
        session.Preview.EnumerateRunes().Count().ShouldBe(Session.PreviewMaxLength);
    }
}
