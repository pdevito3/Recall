namespace Recall.UnitTests.Domain.Sessions.Content;

using Recall.Server.Domain.Sessions.Content;
using Shouldly;

/// <summary>"I do not" and "I do ~~not~~" say opposite things, so the plain text keeps the strike of a word.</summary>
public class ProseMirrorToPlainTextTests
{
    private const string PersonId = "per_ann";

    private static string Tagged(string markdown, string name)
    {
        var json = MarkdownToProseMirror.ConvertToJson(markdown);
        return MentionInsertion.Insert(json, PeopleTagSpans.Spans(ProseMirrorToPlainText.Render(json), name, PersonId, name));
    }

    [Test]
    [Property("Rule", "SES-45")]
    [Arguments("I do ~~not~~ love Ann.")]
    [Arguments("~~All of it.~~")]
    [Arguments("~~Gone~~ and ~~gone too~~ but kept.")]
    public void a_struck_word_keeps_its_strike_in_the_plain_text(string markdown)
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson(markdown));

        // Assert
        plain.ShouldBe(markdown);
    }

    [Test]
    [Property("Rule", "SES-45")]
    public void one_strike_across_other_marks_is_one_run()
    {
        // Act
        var plain = ProseMirrorToPlainText.Render(MarkdownToProseMirror.ConvertToJson("~~a **b** _c_~~ d"));

        // Assert
        plain.ShouldBe("~~a b c~~ d");
    }

    [Test]
    [Property("Rule", "SES-45")]
    public void a_tag_next_to_a_struck_word_lands_on_the_name_and_the_strike_stays()
    {
        // Act
        var tagged = Tagged("I do ~~not~~ love Ann.", "Ann");

        // Assert
        PersonMentions.PersonIdsIn(tagged).ShouldBe([PersonId]);
        ProseMirrorToPlainText.Render(tagged).ShouldBe("I do ~~not~~ love Ann.");
    }

    [Test]
    [Property("Rule", "SES-45")]
    public void a_tag_of_a_struck_name_keeps_the_strike()
    {
        // Act
        var tagged = Tagged("I met ~~Ann~~ Bea today.", "Ann");

        // Assert
        PersonMentions.PersonIdsIn(tagged).ShouldBe([PersonId]);
        ProseMirrorToPlainText.Render(tagged).ShouldBe("I met ~~Ann~~ Bea today.");
    }
}
