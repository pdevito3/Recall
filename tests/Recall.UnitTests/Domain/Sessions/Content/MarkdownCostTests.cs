namespace Recall.UnitTests.Domain.Sessions.Content;

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Content;
using Shouldly;

/// <summary>One text must not stop or kill the server. A text above the bound stays as the source, with each word.</summary>
public class MarkdownCostTests
{
    private const long MostBytesOfOneConversion = 256 * 1024 * 1024;

    private static string Repeat(string text, int count) => new StringBuilder(text.Length * count).Insert(0, text, count).ToString();

    public static IEnumerable<Func<string>> EachCostlyText()
    {
        string[] texts =
        [
            Repeat("[a](", 250_000),
            Repeat("[a](", 12_000),
            Repeat("[a]: b\n", 142_000),
            Repeat("|", 990_000),
            Repeat("|a|b|\n", 165_000),
            Repeat("a\n", 500_000),
            Repeat("a\n\n", 333_000),
            Repeat("- a\n", 250_000),
            Repeat("*", 499_000) + "a" + Repeat("*", 499_000),
            Repeat("[a](x ", 66_000),
        ];
        return texts.Select<string, Func<string>>(text => () => text);
    }

    [Test]
    [MethodDataSource(nameof(EachCostlyText))]
    public void a_text_above_the_bound_stays_as_the_source_at_a_bounded_memory(string markdown)
    {
        // Arrange
        MarkdownCost.IsAboveBound(markdown).ShouldBeTrue();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        var json = MarkdownToProseMirror.ConvertToJson(markdown);

        // Assert
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(MostBytesOfOneConversion);
        var lines = markdown.Split('\n').Where(line => line.Length > 0).ToList();
        var nodes = JsonNode.Parse(json)!["content"]!.AsArray();
        nodes.Count.ShouldBe(lines.Count);
        nodes[0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe(lines[0]);
        nodes[^1]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe(lines[^1]);
    }

    [Test]
    public void the_form_of_a_text_above_the_bound_is_the_form_of_any_other_source_text()
    {
        // Arrange: a fence with an argument goes to the source through the rule of the words, under the bound.
        const string line = "```js \"quoted\" café \\ \t tab";
        var above = line + "\r\n" + "   \n" + Repeat("|", MarkdownCost.MostMarks + 1);

        // Act
        var json = MarkdownToProseMirror.ConvertToJson(above);

        // Assert
        var nodes = JsonNode.Parse(json)!["content"]!.AsArray();
        nodes.Count.ShouldBe(2);
        nodes[0]!["type"]!.GetValue<string>().ShouldBe("paragraph");
        nodes[0]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("text");
        nodes[0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe(line);
        json.ShouldContain("café");
    }

    [Test]
    public void marks_that_nest_past_the_limit_end_in_the_source_on_a_small_stack()
    {
        // Arrange: this stays under each limit of the bound, and it reaches the walk of the tree. A stack overflow ends the process.
        var markdown = Repeat("*", 16_000) + "a" + Repeat("*", 16_000);
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();
        string? json = null;

        // Act
        var thread = new Thread(() => json = MarkdownToProseMirror.ConvertToJson(markdown), maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        // Assert
        ProseMirrorToPlainText.Render(json!).Trim().ShouldBe(markdown);
    }

    [Test]
    [Arguments("Costs 5, and [a link](https://example.com) with *marks*.")]
    [Arguments("a paragraph\n\n- an item\n- an item\n\n# a heading")]
    public void a_text_of_a_person_is_under_the_bound(string markdown)
    {
        // Act and assert
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();
    }

    [Test]
    public void links_that_can_fit_in_a_draft_are_under_the_bound()
    {
        // Arrange
        var markdown = Repeat("a line of words with [a link](https://example.com/page) in a block of its own\n\n", 3_000);
        MarkdownToProseMirror.ConvertToJson(markdown).Length.ShouldBeLessThan(Session.DraftMaxLength);

        // Act and assert
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();
    }

    [Test]
    public void the_work_of_links_stops_at_a_blank_line()
    {
        // Arrange: the same starts in one block read to its end, and in many blocks read only their own block.
        var block = Repeat("[a](", 1_200);
        var inOne = Repeat(block, 10);
        var inMany = Repeat(block + "\n\n", 10);

        // Act and assert
        MarkdownCost.IsAboveBound(inOne).ShouldBeTrue();
        MarkdownCost.IsAboveBound(inMany).ShouldBeFalse();
    }

    [Test]
    public void an_out_of_memory_in_a_conversion_leaves_the_source_and_no_retry()
    {
        // Act
#pragma warning disable CA2201 // The conversion catches this exact type, which an allocation throws, and a test cannot exhaust the memory of the runner.
        var json = MarkdownToProseMirror.ConvertToJson("**words**", _ => throw new OutOfMemoryException());
#pragma warning restore CA2201

        // Assert
        ProseMirrorToPlainText.Render(json).Trim().ShouldBe("**words**");
    }

    [Test]
    public void a_regex_at_its_time_limit_leaves_the_source()
    {
        // Act
        var json = MarkdownToProseMirror.ConvertToJson("**words**", _ => throw new RegexMatchTimeoutException());

        // Assert
        ProseMirrorToPlainText.Render(json).Trim().ShouldBe("**words**");
    }

    [Test]
    public void empty_words_give_the_empty_document()
    {
        // Act and assert
        MarkdownToProseMirror.ConvertToJson("  \n ").ShouldBe("""{"type":"doc","content":[]}""");
        MarkdownToProseMirror.ConvertToJson(null).ShouldBe("""{"type":"doc","content":[]}""");
    }
}
