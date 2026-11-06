namespace Recall.UnitTests.Domain.Sessions.Content;

using System.Text;
using System.Text.Json;
using Recall.Server.Domain.Sessions.Content;
using Shouldly;

public class PeopleTagSpansTests
{
    [Test]
    public void a_preview_keeps_the_sentence_around_the_name()
    {
        // Act
        var contexts = PeopleTagSpans.Contexts("I slept. I met Sam at noon. It rained.", "Sam");

        // Assert
        contexts.ShouldBe(["I met Sam at noon."]);
    }

    [Test]
    public void words_with_no_sentence_end_give_a_short_preview()
    {
        // Arrange
        var words = string.Join(' ', Enumerable.Repeat("Sam", 5_000));

        // Act
        var contexts = PeopleTagSpans.Contexts(words, "Sam");

        // Assert
        contexts.ShouldAllBe(context => context.Length <= 610);
    }

    [Test]
    public void two_names_in_one_long_sentence_give_one_preview()
    {
        // Arrange
        var words = $"{new string('x', 400)} Sam {new string('y', 100)} Sam {new string('z', 400)}";

        // Act
        var contexts = PeopleTagSpans.Contexts(words, "Sam");

        // Assert
        contexts.Count.ShouldBe(1);
    }

    [Test]
    public void a_sentence_after_many_names_still_gets_a_preview()
    {
        // Arrange
        var sentence = string.Join(' ', Enumerable.Repeat("Sam", 50));
        var words = $"{sentence}. {sentence}, again. Sam left.";

        // Act
        var contexts = PeopleTagSpans.Contexts(words, "Sam");

        // Assert
        contexts.Count.ShouldBe(3);
        contexts[2].ShouldBe("Sam left.");
    }

    [Test]
    public void a_preview_never_cuts_a_character_in_two()
    {
        // Arrange
        const string emoji = "\U0001F600";
        var cases = Enumerable.Range(290, 20).SelectMany(width => new[]
        {
            $"{emoji}{new string('x', width)} Sam",
            $"Sam {new string('x', width)}{emoji}",
        });

        foreach (var words in cases)
        {
            // Act
            var contexts = PeopleTagSpans.Contexts(words, "Sam");

            // Assert
            contexts.ShouldAllBe(context => context.EnumerateRunes().All(rune => rune != Rune.ReplacementChar));
            JsonSerializer.Serialize(contexts).ShouldNotBeNull();
        }
    }

    [Test]
    public void many_sentences_give_a_limited_number_of_previews()
    {
        // Arrange
        var words = string.Concat(Enumerable.Range(0, 1_000).Select(number => $"Sam said {number}. "));

        // Act
        var contexts = PeopleTagSpans.Contexts(words, "Sam");

        // Assert
        contexts.Count.ShouldBe(100);
    }
}
