namespace Recall.Server.Domain.Sessions.Content;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

/// <summary>
/// The check behind the rule of <see cref="MarkdownToProseMirror"/>. It counts each word and each sign that is not a mark of
/// Markdown, because a lost line can hold only words that stay elsewhere. It reads the words of a block over the edges of
/// its text nodes, because a mark inside a word changes the word: "2*3*4" must not show as "234".
/// </summary>
internal static partial class MarkdownWords
{
    public static bool Keeps(string markdown, MarkdownDocument parsed, JsonObject document)
    {
        var inDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        CountDocument(document, inDocument);

        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        CountWords(EntityForm().Replace(ItemMarker().Replace(markdown, " "), entity => WebUtility.HtmlDecode(entity.Value)), missing, 1);

        foreach (var link in parsed.Descendants<LinkInline>())
            CountCopiesOf(link, missing);

        // An autolink is its text and its address, and the source has the words once. A refused address is text only.
        foreach (var address in parsed.Descendants<AutolinkInline>().Select(link => link.IsEmail ? $"mailto:{link.Url}" : link.Url).Where(LinkAddress.IsAllowed))
            CountWords(address, missing, 1);

        return missing.All(word => word.Value <= inDocument.GetValueOrDefault(word.Key))
            && !parsed.Descendants<FencedCodeBlock>().Any(fence => !string.IsNullOrEmpty(fence.Arguments))
            && parsed.Descendants<ListBlock>().All(ItemsKeepTheirMeaning)
            && parsed.Descendants<Table>().All(EachCellHoldsWords);
    }

    /// <summary>The document can hold a word of a link twice when the source has it once, and that must not hide a lost word.</summary>
    private static void CountCopiesOf(LinkInline link, Dictionary<string, int> missing)
    {
        // The label of "[text][label]" stays in the definition, and the document needs it only once.
        if (link.LocalLabel == LocalLabel.Local)
            CountWords(link.Label!, missing, -1);

        // A link that reads a definition has the address and the title, and the definition stays as text.
        if (link.Reference is not null)
            CountWords($"{link.Url} {link.Title}", missing, 1);

        // A link with no visible text has its address as text and as the address. A refused address is text only.
        if (link.FirstChild is null && link.Url is not null && LinkAddress.IsAllowed(link.Url))
            CountWords(link.Url, missing, 1);
    }

    /// <summary>A list holds no number of an item, and a page counts from 1 or shows no number. So only "1. 2. 3." and "1. 1. 1." can go in the document, and an ordered task list cannot.</summary>
    private static bool NumbersStayOnAPage(List<ListItemBlock> items)
        => !items.All(MarkdownToProseMirror.IsTaskItem)
            && (items.All(item => item.Order == 1) || items.Select((item, index) => item.Order == index + 1).All(counts => counts));

    /// <summary>A list that mixes tasks has no place for the state of a task, and "+ 5" reads as a sum and not as an item.</summary>
    private static bool ItemsKeepTheirMeaning(ListBlock list)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        if (list.IsOrdered)
            return NumbersStayOnAPage(items);

        return list.BulletType != '+' && (items.All(MarkdownToProseMirror.IsTaskItem) || !items.Any(MarkdownToProseMirror.IsTaskItem));
    }

    /// <summary>A row is words with a space between cells, so an empty cell moves each later word to another column.</summary>
    private static bool EachCellHoldsWords(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        return rows.All(row => row.Count == rows[0].Count
            && row.OfType<TableCell>().All(cell => cell.Descendants<LeafBlock>().Any(leaf => leaf.Inline?.FirstChild is not null)));
    }

    private static void CountDocument(JsonObject node, Dictionary<string, int> counts)
    {
        if (node["attrs"] is JsonObject attrs && attrs["language"] is JsonValue language)
            CountWords(language.GetValue<string>(), counts, 1);

        if (node["content"] is not JsonArray children)
            return;

        var shown = new StringBuilder();
        // One link can be many text nodes, and its address counts once.
        string? address = null;
        foreach (var child in children.OfType<JsonObject>())
        {
            if (child["text"] is not JsonValue text)
            {
                CountDocument(child, counts);
                continue;
            }

            shown.Append(text.GetValue<string>());
            var next = AddressOf(child);
            if (next is not null && next != address)
                CountWords(next, counts, 1);
            address = next;
        }

        CountWords(shown.ToString(), counts, 1);
    }

    private static string? AddressOf(JsonObject node)
        => (node["marks"] as JsonArray)?.OfType<JsonObject>().Select(mark => mark["attrs"]?["href"]).OfType<JsonValue>().FirstOrDefault()?.GetValue<string>();

    private static void CountWords(string text, Dictionary<string, int> counts, int change)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) && !HasNoSpaces(rune))
            {
                word.Append(rune.ToString());
                continue;
            }

            Add(word, counts, change);
            if (!Rune.IsWhiteSpace(rune) && !IsMarkOfTheForm(rune))
            {
                word.Append(rune.ToString());
                Add(word, counts, change);
            }
        }

        Add(word, counts, change);
    }

    private static bool IsMarkOfTheForm(Rune rune) => rune.IsAscii && "*_~=+\\#><-|[]()!:`".Contains((char)rune.Value);

    /// <summary>
    /// Thai, Lao, Tibetan, Myanmar, Khmer, Han, and Kana write no space between words, so a mark there does not join two words. Each letter
    /// is its own count. Hangul writes spaces, so it counts as Latin does.
    /// </summary>
    private static bool HasNoSpaces(Rune rune) => rune.Value is
        (>= 0x0E00 and <= 0x109F)
        or (>= 0x1780 and <= 0x17FF)
        or (>= 0x2E80 and <= 0x312F)
        or (>= 0x3190 and <= 0x9FFF)
        or (>= 0xF900 and <= 0xFAFF)
        or (>= 0xFF65 and <= 0xFF9F)
        or (>= 0x20000 and <= 0x3FFFF);

    private static void Add(StringBuilder word, Dictionary<string, int> counts, int change)
    {
        if (word.Length == 0)
            return;

        var key = word.ToString();
        counts[key] = counts.GetValueOrDefault(key) + change;
        word.Clear();
    }

    // The marker of a list item, with the "[x]" of a task. NumbersStayOnAPage decides on the number of an ordered list.
    [GeneratedRegex(@"^(?:[ \t>]*(?:[-+*]|[0-9]{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)+", RegexOptions.Multiline, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex ItemMarker();

    [GeneratedRegex(@"&#?[a-zA-Z0-9]{1,40};", RegexOptions.None, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex EntityForm();
}
