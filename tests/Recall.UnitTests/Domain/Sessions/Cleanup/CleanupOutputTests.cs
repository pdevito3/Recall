namespace Recall.UnitTests.Domain.Sessions.Cleanup;

using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Cleanup;
using Shouldly;

public class CleanupOutputTests
{
    [Test]
    public void words_around_the_object_do_not_matter()
    {
        // Act
        var output = CleanupOutput.Parse("Here it is:\n{\"cleanedMarkdown\":\"Clean\",\"synopsis\":\" Recap \",\"topicSuggestions\":[\"work\"],\"peopleProposal\":[\"Sam\"],\"moodSuggestions\":[\"calm\"]}\nDone.");

        // Assert
        output.ShouldNotBeNull();
        output.CleanedMarkdown.ShouldBe("Clean");
        output.Synopsis.ShouldBe("Recap");
        output.TopicSuggestions.ShouldBe(["work"]);
        output.PersonNames.ShouldBe(["Sam"]);
        output.MoodSuggestions.ShouldBe(["Calm"]);
    }

    [Test]
    [Arguments("")]
    [Arguments("no object here")]
    [Arguments("{\"synopsis\":\"no cleaned words\"}")]
    [Arguments("{\"cleanedMarkdown\":\"   \"}")]
    [Arguments("{\"cleanedMarkdown\":\"cut")]
    [Arguments("{\"cleanedMarkdown\":12}")]
    public void an_answer_with_no_cleaned_words_is_no_output(string answer)
    {
        // Arrange & Act & Assert
        CleanupOutput.Parse(answer).ShouldBeNull();
    }

    [Test]
    public void a_word_with_a_null_character_is_no_output_so_it_cannot_vanish_in_the_database()
    {
        // Arrange & Act & Assert
        CleanupOutput.Checked("clean\0words", "recap", [], [], []).ShouldBeNull();
    }

    [Test]
    public void cleaned_words_above_the_limit_are_no_output()
    {
        // Arrange & Act & Assert
        CleanupOutput.Checked(new string('x', Session.DraftMaxLength + 1), "recap", [], [], []).ShouldBeNull();
    }

    [Test]
    public void a_synopsis_above_the_limit_is_no_output()
    {
        // Arrange & Act & Assert
        CleanupOutput.Checked("clean", new string('x', Session.SynopsisMaxLength + 1), [], [], []).ShouldBeNull();
    }

    [Test]
    public void each_list_drops_a_value_that_it_cannot_store()
    {
        // Act
        var output = CleanupOutput.Checked("clean", "recap", ["  ", null, "work", new string('x', Session.MetadataValueMaxLength + 1)], ["bad\0name", "Sam"], ["calm", "Calm"]);

        // Assert
        output.ShouldNotBeNull();
        output.TopicSuggestions.ShouldBe(["work"]);
        output.PersonNames.ShouldBe(["Sam"]);
        output.MoodSuggestions.ShouldBe(["Calm"]);
    }

    [Test]
    public void a_hard_replacement_changes_whole_words_in_each_case_and_no_part_of_a_word()
    {
        // Arrange
        var correction = Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: true);

        // Act
        var replaced = CleanupCompletion.WithHardReplacements("I set up cooper netties, Cooper Netties, and scooper netties.", [correction]);

        // Assert
        replaced.ShouldBe("I set up Kubernetes, Kubernetes, and scooper netties.");
    }

    [Test]
    public void a_hint_changes_no_word()
    {
        // Arrange
        var correction = Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false);

        // Act
        var replaced = CleanupCompletion.WithHardReplacements("I set up cooper netties.", [correction]);

        // Assert
        replaced.ShouldBe("I set up cooper netties.");
    }
}
