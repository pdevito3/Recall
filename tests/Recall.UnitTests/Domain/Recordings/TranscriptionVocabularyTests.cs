namespace Recall.UnitTests.Domain.Recordings;

using System.Text;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;
using static Recall.Server.Domain.Recordings.Transcription.TranscriptionVocabulary;

public class TranscriptionVocabularyTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static KnownWord Word(string term, int minute = 0) => new(term, Day.AddMinutes(minute));

    [Test]
    [Property("Rule", "REC-32")]
    public void the_prompt_lists_each_term_once_in_the_order_of_its_spelling()
    {
        // Act
        var prompt = PromptOf([Word("Radancy"), Word(" Kubernetes "), Word("radancy"), Word("DeVito"), Word("Radancy")]);

        // Assert
        prompt.Text.ShouldBe("DeVito, Kubernetes, Radancy");
        prompt.Terms.ShouldBe(3);
        prompt.Sent.ShouldBe(3);
        prompt.Bytes.ShouldBe(Encoding.UTF8.GetByteCount("DeVito, Kubernetes, Radancy"));
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void the_order_of_the_rows_does_not_change_the_prompt()
    {
        // Act
        var one = PromptOf([Word("Radancy", 3), Word("Kubernetes", 1), Word("DeVito", 2)]);
        var other = PromptOf([Word("DeVito", 2), Word("Radancy", 3), Word("Kubernetes", 1)]);

        // Assert
        one.ShouldBe(other);
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void no_term_gives_no_prompt()
    {
        // Act and assert
        PromptOf([]).ShouldBe(new Prompt(null, 0, 0, 0));
        PromptOf([Word("  ")]).Text.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void a_long_list_keeps_the_newest_terms_within_the_limit_in_the_order_of_their_spelling()
    {
        // Arrange
        var words = Enumerable.Range(0, 100).Select(i => Word($"Tm{i:D2}", i)).ToList();

        // Act
        var prompt = PromptOf(words);

        // Assert
        var sent = prompt.Text.ShouldNotBeNull().Split(", ");
        Encoding.UTF8.GetByteCount(prompt.Text).ShouldBeLessThanOrEqualTo(MaxBytes);
        prompt.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(prompt.Text));
        prompt.Terms.ShouldBe(100);
        prompt.Sent.ShouldBe(sent.Length);
        sent.ShouldBe(Enumerable.Range(100 - sent.Length, sent.Length).Select(i => $"Tm{i:D2}"));
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void a_term_that_does_not_fit_leaves_room_for_an_older_shorter_one()
    {
        // Arrange
        var words = Enumerable.Range(0, 37).Select(i => Word($"Tm{i:D2}", 100 + i)).Append(Word(new string('a', 300), 200)).Append(Word("Zz", 1)).Append(Word("Yy", 0));

        // Act
        var prompt = PromptOf(words);

        // Assert
        prompt.Text.ShouldNotBeNull().ShouldNotContain("aaa");
        prompt.Text.ShouldEndWith(", Zz");
        prompt.Text.ShouldNotContain("Yy");
        prompt.Bytes.ShouldBe(MaxBytes);
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void the_limit_counts_bytes_so_a_script_of_wide_characters_stays_inside_it()
    {
        // Arrange
        var words = Enumerable.Range(0, 60).Select(i => Word($"東京{i:D2}", i)).ToList();

        // Act
        var prompt = PromptOf(words);

        // Assert
        Encoding.UTF8.GetByteCount(prompt.Text.ShouldNotBeNull()).ShouldBeLessThanOrEqualTo(MaxBytes);
        prompt.Text.ShouldEndWith("東京58, 東京59");
    }

    [Test]
    [Property("Rule", "REC-32")]
    public void a_record_that_carries_the_prompt_does_not_print_its_terms()
    {
        // Arrange
        const string vocabulary = "Radancy, Zyntriqix";
        var start = new BeginTranscription.Start("audio", "work", 0, null, null, null, vocabulary);
        var input = new TranscriptionInput("audio", "work", 0, null, vocabulary);

        // Act
        var printed = new[] { start.ToString(), input.ToString(), $"{start} {input}" };

        // Assert
        printed.ShouldAllBe(text => !text.Contains("Radancy") && !text.Contains("Zyntriqix"));
    }
}
