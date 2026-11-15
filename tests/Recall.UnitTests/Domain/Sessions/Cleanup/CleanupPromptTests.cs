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
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: true);

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
        var prompt = CleanupPrompt.InstructionsWith([], removeFillerWords: false);

        // Assert
        prompt.ShouldContain("Keep each filler word, hesitation sound, stutter, repeated word, and false start where the person said it");
        prompt.ShouldNotContain("Remove filler words");
        prompt.ShouldNotContain("Remove a false start");
    }

    [Test]
    [Property("Rule", "SES-51")]
    [Arguments(true)]
    [Arguments(false)]
    public void either_speech_rule_keeps_the_answer_shape_and_the_hints(bool removeFillerWords)
    {
        // Arrange
        Correction[] corrections = [Correction.Create("ten_1", "usr_1", "Kubernetes", ["cooper netties"], hardReplace: false)];

        // Act
        var prompt = CleanupPrompt.InstructionsWith(corrections, removeFillerWords);

        // Assert
        prompt.ShouldContain("Answer with ONLY one JSON object");
        prompt.ShouldContain("\"Kubernetes\", often misheard as: cooper netties");
    }
}
