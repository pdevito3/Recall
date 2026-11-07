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

    private static string GridTable(string cell)
    {
        var border = "+" + Repeat("-", cell.Length + 2) + "+";
        return $"{border}\n| {cell} |\n{border}";
    }

    /// <summary>The cell of the second column goes on over the border of the first, so its edges are "+".</summary>
    private static string CellOverABorder(string cell)
    {
        var border = "+--+" + Repeat("-", cell.Length + 2) + "+";
        var blank = Repeat(" ", cell.Length + 1);
        return $"{border}\n|a |c{blank}|\n+--+ {cell} +\n{border}";
    }

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
            Repeat("- ", 300_000) + "x",
            Repeat("- ", 499_990) + "x",
            Repeat("> ", 300_000) + "x",
            Repeat(">", 990_000) + "x",
            Repeat("1. ", 300_000) + "x",
            Repeat("1) ", 300_000) + "x",
            Repeat("- * ", 30_000) + "-",
            Repeat("- > 1. ", 100_000) + "x",
            "- a\n" + Repeat(" ", 6) + Repeat("- ", 300_000) + "x",
            "> - a\n>     " + Repeat("- ", 300_000) + "x",
            "> 1. a\n>      " + Repeat("1. ", 200_000) + "x",
            "> 123456789. a\n>              " + Repeat("- ", 300_000) + "x",
            "> * a\n>     " + Repeat("+ ", 300_000) + "x",
            "> - a\n>\t  " + Repeat("- ", 300_000) + "x",
            "> > - a\n> >     " + Repeat("- ", 300_000) + "x",
            "> - - a\n>       " + Repeat("- ", 300_000) + "x",
            "- > - a\n  >     " + Repeat("- ", 300_000) + "x",
            "> - a\nb\n>     " + Repeat("- ", 300_000) + "x",
            string.Concat(Enumerable.Range(0, 170).Select(line => Repeat(" ", line * 64) + Repeat("- ", 32) + "x\n")),
            "a\r" + Repeat("- ", 300_000) + "x",
            "a\r" + Repeat("> ", 300_000) + "x",
            "> - a\r>     " + Repeat("- ", 300_000) + "x",
            "> - a\r\n>     " + Repeat("- ", 300_000) + "x",
            string.Concat(Enumerable.Range(0, 170).Select(line => Repeat(" ", line * 64) + Repeat("- ", 32) + "x\r")),
            GridTable(Repeat("- ", 150_000) + "x"),
            "> " + GridTable(Repeat("> ", 150_000) + "x").Replace("\n", "\n> ", StringComparison.Ordinal),
            CellOverABorder(Repeat("- ", 120_000) + "x"),
            GridTable("\u00a0" + Repeat("- ", 150_000) + "x"),
        ];
        return texts.Select<string, Func<string>>(text => () => text);
    }

    [Test]
    [MethodDataSource(nameof(EachCostlyText))]
    public void a_text_above_the_bound_stays_as_the_source_at_a_bounded_memory(string markdown)
    {
        // Arrange: the text does not get to the parser.
        var parses = false;
        MarkdownToProseMirror.ConvertToJson(markdown, _ =>
        {
            parses = true;
            return "";
        });
        parses.ShouldBeFalse();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        var json = MarkdownToProseMirror.ConvertToJson(markdown);

        // Assert
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(MostBytesOfOneConversion);
        var lines = markdown.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).Where(line => line.Length > 0).ToList();
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
    [Arguments("> ")]
    [Arguments("- ")]
    [Arguments("1. ")]
    public void blocks_that_nest_to_the_bound_already_end_in_the_source(string sign)
    {
        // Arrange: so the bound on the nesting of blocks takes no form from the document.
        var markdown = Repeat(sign, MarkdownCost.DeepestBlock) + "x";
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();

        // Act
        var json = MarkdownToProseMirror.ConvertToJson(markdown);

        // Assert
        ProseMirrorToPlainText.Render(json).Trim().ShouldBe(markdown.Trim());
    }

    [Test]
    [Arguments(63, false)]
    [Arguments(64, true)]
    public void the_indent_after_a_quote_counts_to_the_bound(int indentPairs, bool isAbove)
    {
        // Arrange: the quote is one block, and each two columns after it can hold one list item that goes on.
        var markdown = "> - a\n>" + Repeat("  ", indentPairs) + "x";

        // Act and assert
        MarkdownCost.IsAboveBound(markdown).ShouldBe(isAbove);
    }

    [Test]
    [Arguments("- - - - -- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -")]
    [Arguments("> * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * *")]
    [Arguments("-      - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - x")]
    [Arguments("1234567890. 1. 2. 3. 4. 5. 6. 7. 8. 9. 10. 11. 12. 13. 14. 15. 16. 17. 18. 19. 20. 21. 22. 23. 24. 25. 26. 27. 28. 29. 30. 31. 32. 33.")]
    public void signs_that_open_no_block_are_under_the_bound(string markdown)
    {
        // Act and assert: a thematic break, indented code, and a number that no list can start with.
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();
    }

    [Test]
    public void a_deep_indent_of_code_is_under_the_bound()
    {
        // Arrange
        var markdown = "```\n" + Repeat("\t", 16) + "return;\n" + Repeat(" ", 120) + "}\n```";

        // Act and assert
        MarkdownCost.IsAboveBound(markdown).ShouldBeFalse();
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
