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
        return marks > MostMarks || linkWork > MostLinkWork;
    }

    private static bool IsMark(char character) => character is '|' or '*' or '_' or '~' or '=' or '+' or '`' or '[' or ']' or '<' or '&' or '#' or '!' or '\\';
}
