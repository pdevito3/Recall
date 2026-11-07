namespace Recall.UnitTests.Domain.Sessions.Content;

using System.Text;
using Markdig;
using Markdig.Syntax;
using Recall.Server.Domain.Sessions.Content;
using Shouldly;

/// <summary>The bound reads the nesting of blocks before the parse, so on each text it must count at least the nesting that the parser makes.</summary>
public class MarkdownDepthPropertyTests
{
    private const int Seed = 20_261_107;
    private const int Texts = 50_000;

    private static readonly string[] Pieces =
    [
        "- ", "* ", "+ ", "1. ", "9) ", "10. ", "123456789. ", "> ", ">", " ", "  ", "   ", "    ", "\t", " \t", "-", "*", "+", "1.",
        "x", "a b", "```", "~~~", "---", "***", "___", "[ ] ", "[x] ", "|", "| a |", "# ", "<div>", "    code", "=", " ",
    ];

    private static readonly string[] LineEnds = ["\n", "\r", "\r\n", "\n\n", "\r\r", "\r\n\r\n", "\n\r"];

    [Test]
    public async Task the_bound_counts_at_least_the_nesting_that_the_parser_makes()
    {
        // Arrange
        var random = new Random(Seed);
        await Console.Out.WriteLineAsync($"seed {Seed}, texts {Texts}");

        for (var index = 0; index < Texts; index++)
        {
            var markdown = Text(random);

            // Act
            var counted = MarkdownCost.DeepestLine(MarkdownToProseMirror.WithLineFeeds(markdown));
            var parsed = Nesting(Markdown.Parse(markdown, MarkdownToProseMirror.Pipeline));

            // Assert
            counted.ShouldBeGreaterThanOrEqualTo(parsed, $"seed {Seed}, text {index}: {Escaped(markdown)}");
        }
    }

    private static string Text(Random random)
    {
        var text = new StringBuilder();
        var lines = random.Next(1, 9);
        for (var line = 0; line < lines; line++)
        {
            if (random.Next(12) == 0)
                text.Append(GridTable(random));
            else
                text.Append(Line(random));

            text.Append(LineEnds[random.Next(LineEnds.Length)]);
        }

        return text.ToString();
    }

    private static string Line(Random random)
    {
        var line = new StringBuilder();
        var pieces = random.Next(0, 14);
        for (var piece = 0; piece < pieces; piece++)
            line.Append(Pieces[random.Next(Pieces.Length)]);
        return line.ToString();
    }

    /// <summary>
    /// A cell of a grid table holds blocks, and the parser reads them as a document of their own. A cell of the second column
    /// can go on over the border of the first, between two "+".
    /// </summary>
    private static string GridTable(Random random)
    {
        var cell = Line(random).Replace("\t", " ", StringComparison.Ordinal).Replace("|", " ", StringComparison.Ordinal).Replace("+", " ", StringComparison.Ordinal);
        var prefix = random.Next(3) == 0 ? "> " : "";
        var border = "+" + new string('-', cell.Length + 2) + "+";
        if (random.Next(2) == 0)
            return string.Join('\n', new[] { border, $"| {cell} |", border }.Select(line => prefix + line));

        var spans = $"+---+ {cell} +";
        var wide = "+---" + border;
        var blank = new string(' ', cell.Length + 1);
        return string.Join('\n', new[] { wide, $"| a |c{blank}|", spans, $"| b |d{blank}|", wide }.Select(line => prefix + line));
    }

    private static int Nesting(Block block)
        => block is ContainerBlock container
            ? (block is QuoteBlock or ListItemBlock ? 1 : 0) + container.Select(Nesting).DefaultIfEmpty(0).Max()
            : 0;

    private static string Escaped(string markdown) => markdown.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
}
