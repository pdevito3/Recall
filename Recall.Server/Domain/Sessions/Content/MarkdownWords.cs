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
        CountWords(EntityForm().Replace(ItemMarker().Replace(markdown, string.Empty), string.Empty), missing, 1);

        // The label of "[text][label]" stays in the definition, and the document needs it only once.
        foreach (var link in parsed.Descendants<LinkInline>().Where(link => link.Label is not null))
            CountWords(link.Label!, missing, -1);

        return missing.All(word => word.Value <= inDocument.GetValueOrDefault(word.Key));
    }

    private static void CountDocument(JsonObject node, Dictionary<string, int> counts)
    {
        if (node["text"] is JsonValue text)
            CountWords(text.GetValue<string>(), counts, 1);
        if (node["attrs"] is JsonObject attrs && attrs["language"] is JsonValue language)
            CountWords(language.GetValue<string>(), counts, 1);
        if (node["marks"] is JsonArray marks)
        {
            foreach (var href in marks.OfType<JsonObject>().Select(mark => mark["attrs"]?["href"]).OfType<JsonValue>())
                CountWords(href.GetValue<string>(), counts, 1);
        }

        if (node["content"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
                CountDocument(child, counts);
        }
    }

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

    // The marker of a list item, with the "[x]" of a task. The start of an ordered list is an attribute, and a later number is not kept.
    [GeneratedRegex(@"^(?:[ \t>]*(?:[-+*]|[0-9]{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)+", RegexOptions.Multiline, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex ItemMarker();

    [GeneratedRegex(@"&#?[a-zA-Z0-9]{1,40};", RegexOptions.None, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex EntityForm();
}
