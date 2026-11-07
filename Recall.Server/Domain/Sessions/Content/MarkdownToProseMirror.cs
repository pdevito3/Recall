using System.Text.Json.Nodes;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Recall.Server.Domain.Sessions.Content;

/// <summary>
/// Pure, deterministic conversion from AI-emitted markdown to canonical Content
/// (ProseMirror/tiptap JSON). Per ADR-0010 the supported set is exactly what markdown can express:
/// nodes doc, paragraph, heading (levels 1-3), bulletList/orderedList/listItem, taskList/taskItem,
/// blockquote, codeBlock, horizontalRule; marks bold, italic, code, strike, underline, highlight, link.
/// (The <c>mention</c> atom is the one sanctioned non-markdown node - produced by the editor and
/// MentionInsertion, never here.) The AI is never asked to emit schema-valid editor JSON - it emits
/// markdown and the server converts here. No DB, no network.
///
/// Unsupported markdown never throws: it degrades to the nearest supported node. The rule for each input: each word of
/// the markdown is in the document, as text, as the address of a link, or as the language of a code block. Two things are
/// not words: the number of a list item after the first, and an HTML entity, which becomes its character.
/// <see cref="MarkdownWords"/> checks the rule on each conversion, and a document that breaks it gives way to the source
/// as text. So a person can see the marks of the source, and cannot lose a word.
/// </summary>
public static class MarkdownToProseMirror
{
    // Pipeline (ADR-0010 parity contract):
    //   - Tables (pipe + grid) so a table parses as a Table block we can flatten to text.
    //   - EmphasisExtras restricted to Strikethrough | Marked | Inserted so ~~x~~ to strike, ==x== to
    //     highlight, ++x++ to underline. Subscript/superscript are deliberately NOT enabled, so a single
    //     ~tilde~ and ^carets^ stay literal text.
    //   - TaskLists so "- [ ]" / "- [x]" parse into list items carrying a TaskList inline marker.
    // Everything else stays CommonMark.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Marked | EmphasisExtraOptions.Inserted)
            .UseTaskLists()
            .Build();

    private static JsonObject Convert(string? markdown)
    {
        var content = new JsonArray();
        var doc = new JsonObject { ["type"] = "doc", ["content"] = content };

        // Empty/whitespace/null markdown to an empty doc, not an error.
        if (string.IsNullOrWhiteSpace(markdown))
            return doc;

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
            AppendBlock(content, block);

        return MarkdownWords.Keeps(markdown, parsed, doc) ? doc : SourceAsText(markdown);
    }

    private static JsonObject SourceAsText(string markdown)
    {
        var content = new JsonArray();
        foreach (var line in markdown.Split('\n'))
            AddParagraphFromText(content, line.TrimEnd('\r'));
        return new JsonObject { ["type"] = "doc", ["content"] = content };
    }

    /// <summary>
    /// Converts markdown to the JSON of the canonical Content doc node. Never throws. The parser and the JSON writer each
    /// refuse very deep nesting, and a model can answer with it, so such words stay as the source.
    /// </summary>
    public static string ConvertToJson(string? markdown)
    {
        try
        {
            return Convert(markdown).ToJsonString(PersonMentions.KeepWords);
        }
        catch (Exception fault) when (fault is ArgumentException or InvalidOperationException)
        {
            return SourceAsText(markdown!).ToJsonString(PersonMentions.KeepWords);
        }
    }

    // ---- blocks -------------------------------------------------------------------------------

    private static void AppendBlock(JsonArray target, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(Heading(heading));
                break;

            case ParagraphBlock paragraph:
                target.Add(Paragraph(paragraph.Inline));
                break;

            case QuoteBlock quote:
                target.Add(Blockquote(quote));
                break;

            case ListBlock list:
                target.Add(List(list));
                break;

            case CodeBlock code: // covers both FencedCodeBlock and indented CodeBlock
                target.Add(CodeBlockNode(code));
                break;

            // Thematic break (---/***) to a horizontalRule node (ADR-0010 re-enables it).
            case ThematicBreakBlock:
                target.Add(new JsonObject { ["type"] = "horizontalRule" });
                break;

            // Raw HTML block degrades to a paragraph carrying its literal text.
            case HtmlBlock html:
                AddParagraphFromText(target, RawLines(html));
                break;

            // Tables (extension) degrade to a paragraph per row, cells joined by spaces, without inventing a table node we
            // don't support.
            case Table table:
                AppendTableAsParagraphs(target, table);
                break;

            // A definition has no place in the schema, and its label and address can be the words of a person.
            case LinkReferenceDefinition definition:
                AddParagraphFromText(target, $"[{definition.Label}]: {definition.Url}{(string.IsNullOrEmpty(definition.Title) ? string.Empty : $" \"{definition.Title}\"")}");
                break;

            // Any other container we don't model: recurse so nested supported blocks still surface.
            case ContainerBlock container:
                foreach (var child in container)
                    AppendBlock(target, child);
                break;

            case LeafBlock { Inline: not null } leaf:
                target.Add(Paragraph(leaf.Inline));
                break;

            case LeafBlock leaf:
                AddParagraphFromText(target, RawLines(leaf));
                break;
        }
    }

    private static JsonObject Heading(HeadingBlock heading)
    {
        // Clamp markdown h4-h6 down to level 3 - our schema only allows 1..3.
        var level = Math.Clamp(heading.Level, 1, 3);
        return new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject { ["level"] = level },
            ["content"] = Inlines(heading.Inline),
        };
    }

    private static JsonObject Paragraph(ContainerInline? inline) =>
        new() { ["type"] = "paragraph", ["content"] = Inlines(inline) };

    // The schema of a quote and of a list item needs a block, and "-" and ">" alone have none.
    private static JsonArray BlocksOf(ContainerBlock container)
    {
        var content = new JsonArray();
        foreach (var child in container)
            AppendBlock(content, child);
        if (content.Count == 0)
            content.Add(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray() });
        return content;
    }

    private static JsonObject Blockquote(QuoteBlock quote)
    {
        return new JsonObject { ["type"] = "blockquote", ["content"] = BlocksOf(quote) };
    }

    private static JsonObject List(ListBlock list)
    {
        // A list is a TASK list when its items carry task markers ("- [ ]" / "- [x]"): Markdig puts a
        // TaskList inline as the first inline of each such item's leading paragraph. We treat a list as
        // a task list when every (non-empty) item is a task item - mixed lists fall back to bullet/
        // ordered with the markers stripped from the task rows.
        var itemBlocks = list.OfType<ListItemBlock>().ToList();
        var isTaskList = itemBlocks.Count > 0 && itemBlocks.All(IsTaskItem);

        var items = new JsonArray();
        foreach (var itemBlock in itemBlocks)
        {
            if (isTaskList)
            {
                items.Add(new JsonObject
                {
                    ["type"] = "taskItem",
                    ["attrs"] = new JsonObject { ["checked"] = TaskItemChecked(itemBlock) },
                    ["content"] = BlocksOf(itemBlock),
                });
            }
            else
            {
                items.Add(new JsonObject { ["type"] = "listItem", ["content"] = BlocksOf(itemBlock) });
            }
        }

        if (isTaskList)
            return new JsonObject { ["type"] = "taskList", ["content"] = items };

        if (!list.IsOrdered)
            return new JsonObject { ["type"] = "bulletList", ["content"] = items };

        // Carry the ordered-list start (markdown "3." to start:3); default to 1.
        var start = int.TryParse(list.OrderedStart, out var parsed) ? parsed : 1;
        return new JsonObject
        {
            ["type"] = "orderedList",
            ["attrs"] = new JsonObject { ["start"] = start },
            ["content"] = items,
        };
    }

    // A task item's leading paragraph starts with a Markdig TaskList inline.
    private static bool IsTaskItem(ListItemBlock item) => FindTaskMarker(item) is not null;

    private static bool TaskItemChecked(ListItemBlock item) => FindTaskMarker(item)?.Checked ?? false;

    private static TaskList? FindTaskMarker(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline: { } inline }
            ? inline.FirstChild as TaskList
            : null;

    private static JsonObject CodeBlockNode(CodeBlock code)
    {
        var text = RawLines(code);
        var node = new JsonObject
        {
            ["type"] = "codeBlock",
            // The schema refuses an empty text node.
            ["content"] = new JsonArray(text.Length > 0 ? [Text(text)] : []),
        };
        if (code is FencedCodeBlock { Info: { Length: > 0 } language })
            node["attrs"] = new JsonObject { ["language"] = language };
        return node;
    }

    private static void AppendTableAsParagraphs(JsonArray target, Table table)
    {
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row)
                continue;

            var line = new JsonArray();
            foreach (var cell in row.OfType<TableCell>())
                AppendInlinesOf(line, cell);
            if (line.Count > 0)
                target.Add(new JsonObject { ["type"] = "paragraph", ["content"] = line });
        }
    }

    // Each cell of a row, or each block of a grid cell, is one run of inlines, with a space between two runs.
    private static void AppendInlinesOf(JsonArray line, ContainerBlock container)
    {
        foreach (var child in container)
        {
            var run = new JsonArray();
            switch (child)
            {
                case ContainerBlock inner:
                    AppendInlinesOf(line, inner);
                    continue;
                case LeafBlock { Inline: { } inlines }:
                    foreach (var inline in inlines)
                        AppendInline(run, inline, marks: null);
                    break;
                case LeafBlock leaf:
                    AddText(run, RawLines(leaf), marks: null);
                    break;
            }

            if (run.Count == 0)
                continue;
            if (line.Count > 0)
                line.Add(Text(" "));
            foreach (var node in run.ToArray())
            {
                run.Remove(node);
                line.Add(node);
            }
        }
    }

    private static void AddParagraphFromText(JsonArray target, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        target.Add(new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = new JsonArray { Text(text) },
        });
    }

    // ---- inlines ------------------------------------------------------------------------------

    private static JsonArray Inlines(ContainerInline? container)
    {
        var array = new JsonArray();
        if (container is null)
            return array;

        foreach (var inline in container)
            AppendInline(array, inline, marks: null);
        return array;
    }

    // marks: the active mark set inherited from enclosing emphasis/code/link wrappers.
    private static void AppendInline(JsonArray target, Inline inline, MarkSet? marks)
    {
        switch (inline)
        {
            // The TaskList marker inline ("[ ]"/"[x]") is structural, not content - never render it as
            // literal text; the checked state is read off the item in List().
            case TaskList:
                break;

            case LiteralInline literal:
                AddText(target, literal.Content.ToString(), marks);
                break;

            // A soft/hard line break inside a paragraph becomes a space so words don't run together.
            case LineBreakInline:
                AddText(target, " ", marks);
                break;

            case CodeInline code:
                AddText(target, code.Content, MarkSet.With(marks, Mark.Of("code")));
                break;

            case EmphasisInline emphasis:
            {
                // Branch on the delimiter CHARACTER, not only the count, so the EmphasisExtras
                // delimiters carry their own marks (Markdig 0.42):
                //   * / _  to 2+ delimiters = bold, 1 = italic   (CommonMark emphasis)
                //   ~      to strikethrough (DelimiterChar '~', count 2)  to strike
                //   =      to marked        (DelimiterChar '=', count 2)  to highlight
                //   +      to inserted      (DelimiterChar '+', count 2)  to underline
                // An unrecognized delimiter char carries NO mark, and its children stay as plain text.
                var mark = emphasis.DelimiterChar switch
                {
                    '*' or '_' => Mark.Of(emphasis.DelimiterCount >= 2 ? "bold" : "italic"),
                    '~' => Mark.Of("strike"),
                    '=' => Mark.Of("highlight"),
                    '+' => Mark.Of("underline"),
                    _ => null,
                };

                var nested = mark is null ? marks : MarkSet.With(marks, mark);
                foreach (var child in emphasis)
                    AppendInline(target, child, nested);
                break;
            }

            // A link emits a link mark on its visible text (matching tiptap's Link default attrs). An image has no node in
            // the set, so its alt text does the same.
            case LinkInline link:
                AppendLink(target, link, link.Url, link.Title, marks);
                break;

            case AutolinkInline autolink:
                AppendAddress(target, autolink.Url, autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url, marks);
                break;

            case HtmlEntityInline entity:
                AddText(target, entity.Transcoded.ToString(), marks);
                break;

            // An unmatched "[" stays a delimiter. Its literal form and the inlines after it are the words of the person.
            case DelimiterInline delimiter:
                AddText(target, delimiter.ToLiteral(), marks);
                foreach (var child in delimiter)
                    AppendInline(target, child, marks);
                break;

            // Raw inline HTML (e.g. <span>) degrades to its literal text rather than being dropped.
            case HtmlInline html:
                AddText(target, html.Tag, marks);
                break;

            // Any other container inline (e.g. an extension wrapper): recurse.
            case ContainerInline other:
                foreach (var child in other)
                    AppendInline(target, child, marks);
                break;

            // MarkdownWords catches a leaf inline with words that no case here knows.
        }
    }

    private static void AppendLink(JsonArray target, ContainerInline link, string? url, string? title, MarkSet? marks)
    {
        var kept = target.Count;
        var nested = LinkAddress.IsAllowed(url) ? MarkSet.With(marks, Mark.Link(url)) : marks;
        foreach (var child in link)
            AppendInline(target, child, nested);

        // A link with no visible text, or an address that no page may open, keeps its address as text.
        if (!string.IsNullOrEmpty(url))
        {
            if (target.Count == kept)
                AppendAddress(target, url, url, marks);
            else if (!LinkAddress.IsAllowed(url))
                AddText(target, $" ({url})", marks);
        }

        if (!string.IsNullOrEmpty(title))
            AddText(target, $" \"{title}\"", marks);
    }

    private static void AppendAddress(JsonArray target, string text, string url, MarkSet? marks)
        => AddText(target, text, LinkAddress.IsAllowed(url) ? MarkSet.With(marks, Mark.Link(url)) : marks);

    private static void AddText(JsonArray target, string? text, MarkSet? marks)
    {
        if (string.IsNullOrEmpty(text))
            return;
        target.Add(Text(text, marks));
    }

    private static JsonObject Text(string text, MarkSet? marks = null)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        var marksArray = marks?.ToJsonArray();
        if (marksArray is { Count: > 0 })
            node["marks"] = marksArray;
        return node;
    }

    private static string RawLines(LeafBlock block) =>
        block.Lines.Lines is null ? string.Empty : string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()));

    // ---- mark model ---------------------------------------------------------------------------

    /// <summary>
    /// A single active mark. Most marks are attr-less (bold/italic/code/strike/underline/highlight) and
    /// are uniquely identified by <see cref="Type"/>; <c>link</c> additionally carries attrs (href +
    /// tiptap's default target/rel). Modeling attrs here lets the mark-emission path stay uniform.
    /// </summary>
    private sealed record Mark(string Type, JsonObject? Attrs = null)
    {
        public static Mark Of(string type) => new(type);

        // Match tiptap's Link extension default attrs (href, target, rel) so the human-editor and
        // AI-cleanup write paths produce identical link shapes. tiptap also emits a null `class`; we
        // omit it (a null attr carries no information and keeps the JSON tidy).
        public static Mark Link(string? href) => new(
            "link",
            new JsonObject
            {
                ["href"] = href ?? string.Empty,
                ["target"] = "_blank",
                ["rel"] = "noopener noreferrer nofollow",
            });

        public JsonObject ToJson()
        {
            var node = new JsonObject { ["type"] = Type };
            if (Attrs is not null)
                node["attrs"] = (JsonObject)Attrs.DeepClone();
            return node;
        }
    }

    /// <summary>
    /// An immutable, deterministically-ordered set of active marks. Adding the same mark <c>Type</c>
    /// twice keeps the first (so nested identical wrappers don't duplicate); emission sorts by mark
    /// type with <see cref="StringComparer.Ordinal"/> so output is stable regardless of nesting order.
    /// </summary>
    private sealed class MarkSet
    {
        private readonly List<Mark> _marks;

        private MarkSet(List<Mark> marks) => _marks = marks;

        public static MarkSet With(MarkSet? existing, Mark mark)
        {
            List<Mark> marks = existing is null ? [] : [.. existing._marks];
            if (marks.All(m => m.Type != mark.Type))
                marks.Add(mark);
            return new MarkSet(marks);
        }

        public JsonArray ToJsonArray()
        {
            var array = new JsonArray();
            foreach (var mark in _marks.OrderBy(m => m.Type, StringComparer.Ordinal))
                array.Add(mark.ToJson());
            return array;
        }
    }
}
