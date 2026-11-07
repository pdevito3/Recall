namespace Recall.Server.Domain.Sessions.Content;

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

/// <summary>
/// The check behind the rule of <see cref="MarkdownToProseMirror"/>. A word is a run of letters and digits, so the marks of
/// Markdown are not words. It counts each word, because a lost line can hold only words that stay elsewhere.
/// </summary>
internal static partial class MarkdownWords
{
    public static bool Keeps(string markdown, MarkdownDocument parsed, JsonObject document)
    {
        var inDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        CountDocument(document, inDocument);

        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        CountWords(EntityForm().Replace(ItemMarker().Replace(markdown, " "), " "), missing, 1);

        foreach (var link in parsed.Descendants<LinkInline>())
            CountCopiesOf(link, missing);

        // An autolink is its text and its address, and the source has the words once. A refused address is text only.
        foreach (var address in parsed.Descendants<AutolinkInline>().Select(link => link.IsEmail ? $"mailto:{link.Url}" : link.Url).Where(LinkAddress.IsAllowed))
            CountWords(address, missing, 1);

        return missing.All(word => word.Value <= inDocument.GetValueOrDefault(word.Key))
            && !parsed.Descendants<FencedCodeBlock>().Any(fence => !string.IsNullOrEmpty(fence.Arguments))
            && parsed.Descendants<ListBlock>().Where(list => list.IsOrdered).All(NumbersStayOnAPage);
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
    private static bool NumbersStayOnAPage(ListBlock list)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        return !items.All(MarkdownToProseMirror.IsTaskItem)
            && (items.All(item => item.Order == 1) || items.Select((item, index) => item.Order == index + 1).All(counts => counts));
    }

    private static void CountDocument(JsonObject node, Dictionary<string, int> counts)
    {
        if (node["text"] is JsonValue text)
            CountWords(text.GetValue<string>(), counts, 1);
        if (node["attrs"] is JsonObject attrs && attrs["language"] is JsonValue language)
            CountWords(language.GetValue<string>(), counts, 1);

        if (node["content"] is JsonArray children)
        {
            // One link can be many text nodes, and its address counts once.
            string? address = null;
            foreach (var child in children.OfType<JsonObject>())
            {
                var next = AddressOf(child);
                if (next is not null && next != address)
                    CountWords(next, counts, 1);
                address = next;
                CountDocument(child, counts);
            }
        }
    }

    private static string? AddressOf(JsonObject node)
        => (node["marks"] as JsonArray)?.OfType<JsonObject>().Select(mark => mark["attrs"]?["href"]).OfType<JsonValue>().FirstOrDefault()?.GetValue<string>();

    private static void CountWords(string text, Dictionary<string, int> counts, int change)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(rune.ToString());
                continue;
            }

            Add(word, counts, change);
        }

        Add(word, counts, change);
    }

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
