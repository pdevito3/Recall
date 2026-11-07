namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Exceptions;
using Shouldly;

public class WordsThatAreNotADocumentTests
{
    private const string PersonId = "per_01";

    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Noon.AddDays(1);

    private static Session NewSession() => Session.Create("ten_1", "usr_1", "device-1", Noon, null, Now);

    private static DraftSave Save(string text) => DraftSave.Of(text, 0, Noon);

    [Test]
    [Property("Rule", "SES-29")]
    [Arguments("""{"a":1,"a":2}""")]
    [Arguments("""{"type":"doc","type":"x"}""")]
    [Arguments("""{"note":"my words"}""")]
    [Arguments("""[1,2]""")]
    [Arguments("""plain words""")]
    public void the_preview_of_words_that_are_not_a_document_is_the_words(string words)
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save(words));

        // Assert
        session.Raw.Text.ShouldBe(words);
        session.Preview.ShouldBe(words);
        session.MentionedPersonIds.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void a_rename_keeps_the_words_and_the_old_label_of_a_text_with_half_of_an_emoji()
    {
        // Arrange
        var words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"cut \ud83d"},{"type":"mention","attrs":{"personId":"per_01","label":"Old"}}]}]}""";
        var session = NewSession();
        session.SaveRaw(Save(words));

        // Act
        session.RenamePerson(PersonId, "New", Now);

        // Assert
        session.Raw.Text.ShouldBe(words);
        session.Raw.DraftRevision.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void a_rename_changes_a_label_that_is_half_of_an_emoji()
    {
        // Arrange
        var words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":{"personId":"per_01","label":"\ud83d"}}]}]}""";
        var session = NewSession();
        session.SaveRaw(Save(words));

        // Act
        session.RenamePerson(PersonId, "New", Now);

        // Assert
        session.Raw.Text.ShouldContain("\"label\":\"New\"");
    }

    [Test]
    [Property("Rule", "SES-29")]
    [Arguments("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":[]}]}]}""")]
    [Arguments("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":1}]}]}""")]
    public void a_mention_with_attributes_of_the_wrong_shape_does_not_stop_a_save(string words)
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(Save(words));

        // Assert
        session.Raw.Text.ShouldBe(words);
        session.MentionedPersonIds.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void half_of_an_emoji_stays_in_the_words_and_the_preview_keeps_the_other_text()
    {
        // Arrange
        const string words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"cut \ud83d"},{"type":"text","text":"the rest"},{"type":"mention","attrs":{"personId":"per_01","label":"Sam"}}]}]}""";
        var session = NewSession();

        // Act
        session.SaveRaw(Save(words));

        // Assert
        session.Raw.Text.ShouldBe(words);
        session.Preview.ShouldBe("the restSam");
        session.MentionedPersonIds.ShouldBe([PersonId]);
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void a_rename_leaves_alone_the_words_that_it_cannot_write_again()
    {
        // Arrange
        const string words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"cut \ud83d"},{"type":"mention","attrs":{"personId":"per_01","label":"Old"}}]}]}""";
        var session = NewSession();
        session.SaveCleaned(Save(words));

        // Act
        session.RenamePerson(PersonId, "New", Now);

        // Assert
        session.Cleaned.Text.ShouldBe(words);
        session.Cleaned.LatestRevision.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void a_rename_replaces_a_label_that_is_half_of_an_emoji()
    {
        // Arrange
        const string words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":{"personId":"per_01","label":"\ud83d"}}]}]}""";
        var session = NewSession();
        session.SaveCleaned(Save(words));

        // Act
        session.RenamePerson(PersonId, "New", Now);

        // Assert
        session.Cleaned.Text.ShouldContain("\"label\":\"New\"");
    }

    [Test]
    [Property("Rule", "SES-29")]
    public void a_preview_has_no_nul()
    {
        // Arrange
        const string words = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a\u0000b"}]}]}""";
        var session = NewSession();

        // Act
        session.SaveRaw(Save(words));

        // Assert
        session.Preview.ShouldBe("ab");
        session.Raw.Text.ShouldBe(words);
    }

    [Test]
    public void a_negative_base_revision_is_refused()
    {
        // Arrange
        const int negative = -1;

        // Act
        var act = () => DraftSave.Of("words", negative, Noon);

        // Assert
        act.ShouldThrow<ValidationException>();
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_save_holds_the_time_of_the_client_in_utc_at_any_clock_so_a_replay_with_another_offset_is_one_save()
    {
        // Arrange
        var fast = Now.AddHours(5);

        // Act
        var save = DraftSave.Of("words", 0, fast.ToOffset(TimeSpan.FromHours(2)));

        // Assert
        save.SavedAt.ShouldBe(fast);
        save.SavedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void a_null_list_of_metadata_is_empty_and_a_null_value_is_dropped()
    {
        // Arrange
        string?[] withNull = ["Calm", null];

        // Act
        var none = SessionMetadata.Of(null, null, null);
        var some = SessionMetadata.Of(null, withNull, withNull);

        // Assert
        none.Moods.ShouldBeEmpty();
        none.UserTopics.ShouldBeEmpty();
        some.Moods.ShouldBe(["Calm"]);
        some.UserTopics.ShouldBe(["Calm"]);
    }

    [Test]
    public void a_null_list_of_mishearings_is_empty_and_a_null_value_is_dropped()
    {
        // Arrange
        string?[] withNull = ["heard", null];

        // Act
        var none = Correction.Create("ten_1", "usr_1", "term", null, false);
        var some = Correction.Create("ten_1", "usr_1", "term", withNull, false);

        // Assert
        none.Mishearings.ShouldBeEmpty();
        some.Mishearings.ShouldBe(["heard"]);
    }

    [Test]
    public void relabel_returns_the_words_as_they_are_when_they_are_not_a_document()
    {
        // Arrange
        const string words = """{"a":1,"a":2}""";

        // Act
        var relabeled = PersonMentions.Relabel(words, PersonId, "New");

        // Assert
        relabeled.ShouldBe(words);
    }
}
