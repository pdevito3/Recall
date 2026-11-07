namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain.People;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Exceptions;
using Shouldly;

public class VisibleTextTests
{
    private static readonly string Bidi = ((char)0x202E).ToString();
    private static readonly string Bell = ((char)0x7).ToString();
    private static readonly string NonJoiner = ((char)0x200C).ToString();
    private static readonly string Joiner = ((char)0x200D).ToString();
    private static readonly string Nul = ((char)0x0).ToString();

    [Test]
    public void a_hidden_character_goes_and_a_letter_of_a_word_stays()
    {
        // Act and Assert
        VisibleText.OneLine($"An{Bidi}na{Bell}\n  Lee ").ShouldBe("Anna Lee");
        VisibleText.OneLine($"می{NonJoiner}روم").ShouldBe($"می{NonJoiner}روم");
        VisibleText.OneLine($"a{Joiner}b").ShouldBe($"a{Joiner}b");
        VisibleText.Without($"line{Bidi}\nend").ShouldBe("line\nend");
        VisibleText.Without("two\twords").ShouldBe("two words");
    }

    [Test]
    public void a_person_name_shows_what_it_holds_and_a_character_that_cannot_be_stored_stays_refused()
    {
        // Act
        var person = Person.Create("ten_1", "usr_1", $" Sam{Bidi} {Bell}Lee ");

        // Assert
        person.Label.ShouldBe("Sam Lee");
        Should.Throw<ValidationException>(() => Person.Create("ten_1", "usr_1", $"Sam{Nul}"));
        Should.Throw<ValidationException>(() => Person.Create("ten_1", "usr_1", $"{Bidi}{Bell}"));
    }

    [Test]
    public void a_topic_and_a_mood_of_a_person_show_what_they_hold()
    {
        // Act
        var metadata = SessionMetadata.Of(null, [$"cal{Bidi}m", $"{Bidi}"], [$" wo{Bell}rk ", $"{Bell}", "home"]);

        // Assert
        metadata.Moods.ShouldBe(["Calm"]);
        metadata.UserTopics.ShouldBe(["work", "home"]);
        Should.Throw<ValidationException>(() => SessionMetadata.Of(null, [], [$"work{Nul}"]));
    }
}
