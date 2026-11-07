namespace Recall.Server.Domain.Sessions.Content;

/// <summary>
/// The bound on the cost of one conversion, as a read of the text before any parse. A timer cannot stop a parse, and a parse
/// that does not stop or a tree that fills the memory stops the server. A text above the bound stays as the source, so no word
/// goes. <c>docs/backend/model-providers.md</c> states the measure behind each limit.
/// </summary>
internal static class MarkdownCost
{
    /// <summary>
    /// The characters that can start a node of the document. A node holds at least 30 characters of JSON, so a text with
    /// more of them has no room in a Draft. The tree costs about 2 KB for each node, so this holds it near 64 MiB.
    /// </summary>
    public const int MostMarks = Session.DraftMaxLength / 30;

    /// <summary>
    /// The parser reads to the end of a block for each "](" and "]:". One unit, one character of one such read, costs at most
    /// 1.5 ns, so this holds the parse of one text under one second.
    /// </summary>
    public const long MostLinkWork = 500_000_000;

    /// <summary>
    /// The blocks that one line can be in: the signs that open a quote or a list item at its start, and half of its indent and
    /// of the indent after each quote, because a list item that goes on takes two columns or more. The parser reads the rest of
    /// the line again for each new list item and the indent again for each one that goes on, so 300 000 signs took 10 s, and
    /// 5 000 blocks in indented lines took 12 s. The JSON writer refuses a depth over 64, and each block takes two levels of it,
    /// so a line in more than 32 blocks stays as the source in any case. The bound is twice that, so an indent of code has room.
    /// </summary>
    public const int DeepestBlock = 64;

    private const string Blank = " \t";

    public static bool IsAboveBound(string markdown)
    {
        var marks = 0;
        var linkStarts = 0L;
        var linkWork = 0L;
        var blockStart = 0;
        var lineIsBlank = true;
        for (var at = 0; at < markdown.Length; at++)
        {
            var character = markdown[at];
            if (character == '\n')
            {
                marks++;
                if (lineIsBlank)
                {
                    linkWork += linkStarts * (at - blockStart);
                    linkStarts = 0;
                    blockStart = at;
                }

                lineIsBlank = true;
                continue;
            }

            if (!char.IsWhiteSpace(character))
                lineIsBlank = false;

            if (IsMark(character))
                marks++;
            if (character == ']' && at + 1 < markdown.Length && markdown[at + 1] is '(' or ':')
                linkStarts++;
        }

        linkWork += linkStarts * (markdown.Length - blockStart);
        return marks > MostMarks || linkWork > MostLinkWork || DeepestLine(markdown) > DeepestBlock;
    }

    /// <summary>The most blocks that one line can be in. A line ends at "\n" only, so the text must have the line ends of <see cref="MarkdownToProseMirror.WithLineFeeds"/>.</summary>
    internal static int DeepestLine(string markdown)
    {
        var deepest = 0;
        for (var start = 0; start < markdown.Length;)
        {
            var end = markdown.IndexOf('\n', start);
            end = end < 0 ? markdown.Length : end;
            deepest = Math.Max(deepest, Depth(markdown.AsSpan(start, end - start)));
            start = end + 1;
        }

        return deepest;
    }

    /// <summary>A cell of a grid table holds blocks of its own, so the blocks of a line can start again after each "|".</summary>
    private static int Depth(ReadOnlySpan<char> line)
    {
        line = line[..ThematicBreakStart(line)];
        var depth = 0;
        foreach (var cell in line.Split('|'))
            depth += DepthFromStart(line[cell]);
        return depth;
    }

    private static int DepthFromStart(ReadOnlySpan<char> line)
    {
        var depth = 0;
        var at = 0;
        while (true)
        {
            // At the start of the line and after a quote, a list item that the line goes on in can take the indent.
            var indent = BlankLength(line, at);
            depth += Columns(line.Slice(at, indent)) / 2;
            at += indent;
            while (at < line.Length && line[at] != '>')
            {
                var after = ListMarkerEnd(line, at);
                if (after < 0)
                    return depth;

                var gap = BlankLength(line, after);
                if (gap == 0 && after < line.Length)
                    return depth;

                depth++;

                // Four columns past the sign of a new list item start indented code, which opens no block.
                if (gap > 4)
                    return depth;
                at = after + gap;
            }

            if (at == line.Length)
                return depth;
            depth++;
            at++;
        }
    }

    /// <summary>Any white space, because the parser trims a cell of a grid table of each kind, and more blanks only count more blocks.</summary>
    private static int BlankLength(ReadOnlySpan<char> line, int at) => line.Length - at - line[at..].TrimStart().Length;

    private static int Columns(ReadOnlySpan<char> blanks) => blanks.Length + (3 * blanks.Count('\t'));

    private static int ListMarkerEnd(ReadOnlySpan<char> line, int at)
    {
        if (line[at] is '-' or '+' or '*')
            return at + 1;

        var digits = 0;
        while (at + digits < line.Length && digits <= 9 && char.IsAsciiDigit(line[at + digits]))
            digits++;
        return digits is > 0 and <= 9 && at + digits < line.Length && line[at + digits] is '.' or ')' ? at + digits + 1 : -1;
    }

    /// <summary>A line that ends in three or more of one of "-", "*", and "_", with only blanks between, ends in a thematic break.</summary>
    private static int ThematicBreakStart(ReadOnlySpan<char> line)
    {
        var at = line.TrimEnd(Blank).Length;
        if (at == 0 || line[at - 1] is not ('-' or '*' or '_'))
            return line.Length;

        var sign = line[at - 1];
        var signs = 0;
        while (at > 0 && (line[at - 1] == sign || line[at - 1] is ' ' or '\t'))
        {
            at--;
            if (line[at] == sign)
                signs++;
        }

        return signs >= 3 ? at : line.Length;
    }

    private static bool IsMark(char character) => character is '|' or '*' or '_' or '~' or '=' or '+' or '`' or '[' or ']' or '<' or '&' or '#' or '!' or '\\';
}
