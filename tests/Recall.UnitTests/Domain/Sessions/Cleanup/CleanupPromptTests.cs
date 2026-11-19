namespace Recall.UnitTests.Domain.Sessions.Cleanup;

using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.Sessions.Cleanup;
using Shouldly;

public class CleanupPromptTests
{
    [Test]
    [Property("Rule", "SES-51")]
    public void with_the_removal_on_the_prompt_removes_speech_noise_but_keeps_a_correct_repeat_and_each_idea()
    {
        // Act
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        prompt.ShouldContain("Speech noise is not an idea, so remove it");
        prompt.ShouldContain("Remove filler words and hesitation sounds, such as \"um\", \"uh\"");
        prompt.ShouldContain("\"like\", \"you know\", \"I mean\", and \"sort of\" only where they carry no meaning");
        prompt.ShouldContain("Remove a stutter or a repeated word");
        prompt.ShouldContain("keep a repeat that is correct, as in \"she had had enough\" or \"I knew that that was wrong\"");
        prompt.ShouldContain("Remove a false start");
        prompt.ShouldContain("Do not add, remove, summarize, or reword ideas.");
        prompt.ShouldNotContain("Keep each filler word");
    }

    [Test]
    [Property("Rule", "SES-51")]
    public void with_the_removal_off_the_prompt_keeps_each_filler_stutter_and_false_start()
    {
        // Act
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: false, writeNumbersAsDigits: true);

        // Assert
        prompt.ShouldContain("Keep each filler word, hesitation sound, stutter, repeated word, and false start where the person said it");
        prompt.ShouldNotContain("Remove filler words");
        prompt.ShouldNotContain("Remove a false start");
    }

    [Test]
    [Property("Rule", "SES-53")]
    public void with_the_number_format_on_the_prompt_writes_a_spoken_number_in_its_written_form_but_keeps_words_where_they_read_better()
    {
        // Act
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        prompt.ShouldContain("write a number, a date, a time, or an amount of money that the person said in words in its usual written form");
        prompt.ShouldContain("A time, a date, an amount of money, and a measurement with a unit symbol take digits: \"three thirty pm\" becomes \"3:30 PM\", \"March third\" becomes \"March 3\", \"twenty dollars\" becomes \"$20\", and \"six percent\" becomes \"6%\".");
        prompt.ShouldContain("A whole count from one to nine stays in words, as in \"two kids\", and a count of 10 or more takes digits, as \"twelve miles\" becomes \"12 miles\".");
        prompt.ShouldContain("The form of a number is not its wording or its content, so this keeps each idea and the voice of the person.");
        prompt.ShouldContain("an approximate or idiomatic number, as in \"one of my friends\", \"a couple of days\", or \"a hundred times better\"");
        prompt.ShouldContain("and a number at the start of a sentence. These exceptions hold over the rules above.");
        prompt.ShouldContain("Do not add, remove, summarize, or reword ideas.");
        prompt.ShouldNotContain("keep each number as they said it");
    }

    [Test]
    [Property("Rule", "SES-53")]
    public void with_the_number_format_off_the_prompt_keeps_each_number_as_the_person_said_it()
    {
        // Act
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: true, writeNumbersAsDigits: false);

        // Assert
        prompt.ShouldContain("The person wants \"cleanedMarkdown\" to keep each number as they said it.");
        prompt.ShouldContain("in words where the Raw text has it in words, as in \"three thirty pm\" and \"twenty dollars\", and in digits where the Raw text has digits");
        prompt.ShouldNotContain("usual written form");
        prompt.ShouldNotContain("\"3:30 PM\"");
    }

    [Test]
    [Property("Rule", "SES-51")]
    [Property("Rule", "SES-53")]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public void each_speech_and_number_rule_keeps_the_answer_shape_and_the_hints(bool removeFillerWords, bool writeNumbersAsDigits)
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords, writeNumbersAsDigits);

        // Assert
        prompt.ShouldContain("Answer with ONLY one JSON object");
        prompt.ShouldContain("\"Kubernetes\", often misheard as: cooper netties");
    }

    [Test]
    [Property("Rule", "SES-51")]
    [Property("Rule", "SES-53")]
    public void with_hints_the_rule_to_change_no_other_words_holds_only_for_the_list_and_the_number_and_speech_rules_still_hold()
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        prompt.ShouldContain("write the term. For this list, change no other words.");
        prompt.ShouldNotContain("write the term. Change no other words.");
        prompt.ShouldContain("in its usual written form");
        prompt.ShouldContain("Remove filler words and hesitation sounds");
    }

    [Test]
    [Property("Rule", "SES-54")]
    public void a_term_with_mishearings_keeps_its_list_and_a_known_word_goes_to_the_model_alone_with_the_rule_to_keep_a_correct_word()
    {
        // Arrange
        Correction[] corrections =
        [
            Correction.Create("ten_1", "usr_1", "Radancy", [], hardReplace: false),
            Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false),
        ];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        LinesOf(prompt).TakeLast(5).ShouldBe(
        [
            "The person keeps a list of terms that are often misheard. When the text holds a mishearing in the sense of the term, write the term. For this list, change no other words.",
            "- \"Kubernetes\", often misheard as: cooper netties",
            "",
            "The person uses these known words. Where the text holds a word or a phrase that sounds like a known word and does not fit the context as written, write the known word as it is spelled here. Do not change a correct word that fits the context.",
            "- \"Radancy\"",
        ]);
    }

    [Test]
    [Property("Rule", "SES-54")]
    public void a_term_with_mishearings_and_no_known_word_gets_no_known_word_block()
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        LinesOf(prompt).TakeLast(2).ShouldBe(
        [
            "The person keeps a list of terms that are often misheard. When the text holds a mishearing in the sense of the term, write the term. For this list, change no other words.",
            "- \"Kubernetes\", often misheard as: cooper netties",
        ]);
        prompt.ShouldNotContain("known word");
    }

    [Test]
    [Property("Rule", "SES-54")]
    public void a_known_word_with_always_replace_on_still_goes_to_the_model()
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Radancy", [], hardReplace: true)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        LinesOf(prompt).TakeLast(2).ShouldBe(
        [
            "The person uses these known words. Where the text holds a word or a phrase that sounds like a known word and does not fit the context as written, write the known word as it is spelled here. Do not change a correct word that fits the context.",
            "- \"Radancy\"",
        ]);
        prompt.ShouldNotContain("often misheard");
    }

    [Test]
    [Property("Rule", "SES-54")]
    public void an_always_replace_term_with_mishearings_does_not_go_to_the_model()
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: true)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords: true, writeNumbersAsDigits: true);

        // Assert
        prompt.ShouldNotContain("Kubernetes");
        prompt.ShouldNotContain("often misheard");
        prompt.ShouldNotContain("known word");
    }

    private static string[] LinesOf(string prompt) => prompt.ReplaceLineEndings("\n").Split('\n');
}
