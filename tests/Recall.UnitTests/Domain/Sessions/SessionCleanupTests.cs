namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Exceptions;
using Shouldly;

/// <summary>The status machine of a Cleanup, and the suggestions and proposals that it leaves.</summary>
public class SessionCleanupTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Session SessionWithRaw()
    {
        var session = Session.Create("ten_1", "usr_1", "device-1", Noon, null, Noon);
        session.SaveRaw(DraftSave.Of("raw words", 0, Noon, Noon));
        return session;
    }

    private static Session CleanSession(IReadOnlyList<string>? topics = null, IReadOnlyList<string>? moods = null)
    {
        var session = SessionWithRaw();
        session.BeginCleanup();
        session.CompleteCleanup(new CleanupOutcome("clean words", "A recap", topics ?? [], moods ?? [], []), session.Raw.DraftRevision, Noon);
        return session;
    }

    [Test]
    public void only_a_begin_can_set_running_and_each_begin_has_its_own_run()
    {
        // Arrange
        var first = SessionWithRaw();
        var second = SessionWithRaw();

        // Act
        var firstRun = first.BeginCleanup();
        var secondRun = second.BeginCleanup();

        // Assert
        first.CleanupRun.ShouldBe(CleanupRunState.Running);
        first.CleanupRunId.ShouldBe(firstRun);
        firstRun.ShouldNotBe(secondRun);
    }

    [Test]
    public void a_second_begin_while_running_is_refused()
    {
        // Arrange
        var session = SessionWithRaw();
        session.BeginCleanup();

        // Act
        var begin = () => session.BeginCleanup();

        // Assert
        begin.ShouldThrow<ValidationException>();
    }

    [Test]
    public void a_complete_or_a_fail_with_no_run_is_refused()
    {
        // Arrange
        var session = SessionWithRaw();

        // Act
        var complete = () => session.CompleteCleanup(new CleanupOutcome("clean", "recap", [], [], []), 1, Noon);
        var fail = () => session.FailCleanup(CleanupFailure.Interrupted);

        // Assert
        complete.ShouldThrow<ValidationException>();
        fail.ShouldThrow<ValidationException>();
        session.CleanupRun.ShouldBe(CleanupRunState.NotRun);
    }

    [Test]
    public void a_complete_keeps_raw_and_a_fail_keeps_cleaned()
    {
        // Arrange
        var session = CleanSession();
        var rawBefore = session.Raw.Text;
        var cleanedBefore = session.Cleaned.Text;
        session.BeginCleanup();

        // Act
        session.FailCleanup(CleanupFailure.ProviderRefused);

        // Assert
        session.Raw.Text.ShouldBe(rawBefore);
        session.Cleaned.Text.ShouldBe(cleanedBefore);
        session.CleanupRun.ShouldBe(CleanupRunState.Failed);
        session.CleanupFailure.ShouldBe(CleanupFailure.ProviderRefused);
    }

    [Test]
    public void a_new_begin_clears_the_old_failure()
    {
        // Arrange
        var session = SessionWithRaw();
        session.BeginCleanup();
        session.FailCleanup(CleanupFailure.ProviderRefused);

        // Act
        session.BeginCleanup();

        // Assert
        session.CleanupFailure.ShouldBe(CleanupFailure.None);
    }

    [Test]
    [Property("Rule", "SES-30")]
    public void an_accept_of_a_topic_that_the_user_holds_adds_no_ai_topic()
    {
        // Arrange
        var session = CleanSession(topics: ["Work"]);
        session.SetMetadata(SessionMetadata.Of(null, [], ["work"]), session.MetadataVersion);
        var version = session.MetadataVersion;

        // Act
        var accepted = session.AcceptTopicSuggestion("Work");

        // Assert
        accepted.ShouldBeTrue();
        session.AiTopics.ShouldBeEmpty();
        session.UserTopics.ShouldBe(["work"]);
        session.TopicSuggestions.ShouldBeEmpty();
        session.MetadataVersion.ShouldBe(version);
    }

    [Test]
    [Property("Rule", "SES-40")]
    public void an_accept_of_a_value_that_is_not_offered_changes_nothing()
    {
        // Arrange
        var session = CleanSession(topics: ["work"], moods: ["Calm"]);
        var version = session.MetadataVersion;

        // Act
        var topic = session.AcceptTopicSuggestion("other");
        var mood = session.AcceptMoodSuggestion("other");

        // Assert
        topic.ShouldBeFalse();
        mood.ShouldBeFalse();
        session.MetadataVersion.ShouldBe(version);
        session.TopicSuggestions.ShouldBe(["work"]);
    }

    [Test]
    public void a_reject_takes_the_suggestion_away_and_no_topic_comes()
    {
        // Arrange
        var session = CleanSession(topics: ["work"], moods: ["Calm"]);

        // Act
        session.RejectTopicSuggestion("WORK");
        session.RejectMoodSuggestion("calm");

        // Assert
        session.TopicSuggestions.ShouldBeEmpty();
        session.MoodSuggestions.ShouldBeEmpty();
        session.AiTopics.ShouldBeEmpty();
        session.Moods.ShouldBeEmpty();
    }
}
