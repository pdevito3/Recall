namespace Recall.UnitTests.Domain.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Recall.Server.Exceptions;
using Shouldly;

public class ImportEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private const string Edit = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"My words"}]}]}""";

    private static ImportAgentLayer Layer(string raw = "Words", string? time = null)
        => ImportAgentLayer.Of("2024-03-31", 0, time, null, raw, null, null);

    private static (ImportBatch Batch, ImportEntry Entry) EntryOfABatch()
    {
        var batch = ImportBatch.Open("ten_1", "usr_1", "Day One", "UTC", Now);
        return (batch, ImportEntry.Add(batch, "entry", Layer()));
    }

    [Test]
    [Property("Rule", "IMP-12")]
    public void a_put_that_changes_raw_after_an_edit_of_the_words_sets_the_flag()
    {
        // Arrange
        var (batch, entry) = EntryOfABatch();
        entry.ChangeReview(ImportReview.Of(false, Edit, null), batch, Now);

        // Act
        entry.PutFromAgent(Layer("New words"));

        // Assert
        entry.WordsChangedAfterEdit.ShouldBeTrue();
        entry.TimeChangedAfterEdit.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-12")]
    public void a_put_with_no_edit_of_the_person_sets_no_flag()
    {
        // Arrange
        var (_, entry) = EntryOfABatch();

        // Act
        entry.PutFromAgent(Layer("New words", "21:00"));

        // Assert
        entry.WordsChangedAfterEdit.ShouldBeFalse();
        entry.TimeChangedAfterEdit.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-12")]
    public void a_new_edit_clears_the_flag()
    {
        // Arrange
        var (batch, entry) = EntryOfABatch();
        entry.ChangeReview(ImportReview.Of(false, Edit, "08:00"), batch, Now);
        entry.PutFromAgent(Layer("New words", "21:00"));

        // Act
        entry.ChangeReview(ImportReview.Of(false, null, "09:00"), batch, Now);

        // Assert
        entry.WordsChangedAfterEdit.ShouldBeFalse();
        entry.TimeChangedAfterEdit.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-6")]
    public void a_put_never_changes_the_review()
    {
        // Arrange
        var (batch, entry) = EntryOfABatch();
        var review = ImportReview.Of(true, Edit, "08:00");
        entry.ChangeReview(review, batch, Now);

        // Act
        entry.PutFromAgent(Layer("New words", "21:00"));

        // Assert
        entry.Review.ShouldBe(review);
    }

    [Test]
    [Property("Rule", "IMP-9")]
    public void a_review_with_a_time_that_puts_the_start_in_the_future_is_refused()
    {
        // Arrange
        var batch = ImportBatch.Open("ten_1", "usr_1", "Day One", "UTC", Now);
        var entry = ImportEntry.Add(batch, "entry", ImportAgentLayer.Of("2026-11-25", 0, "08:00", null, "Words", null, null));

        // Act
        var exception = Should.Throw<ValidationException>(() => entry.ChangeReview(ImportReview.Of(false, null, "13:00"), batch, Now));

        // Assert
        exception.Property.ShouldBe("time");
        exception.Message.ShouldBe("The time of an entry cannot be in the future.");
    }

    [Test]
    [Property("Rule", "IMP-11")]
    public void the_words_of_the_person_win_at_the_accept()
    {
        // Arrange
        var (batch, entry) = EntryOfABatch();
        entry.ChangeReview(ImportReview.Of(false, Edit, null), batch, Now);

        // Act
        var raw = entry.RawAtAccept();

        // Assert
        raw.ShouldBe(Edit);
    }

    [Test]
    [Property("Rule", "IMP-6")]
    [Arguments(" ")]
    [Arguments("""{"type":"doc","content":[]}""")]
    public void an_edit_with_no_words_is_refused(string words)
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => ImportReview.Of(false, words, null));

        // Assert
        exception.Property.ShouldBe("words");
    }
}
