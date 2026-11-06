namespace Recall.UnitTests.Domain.Sessions.Content;

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
