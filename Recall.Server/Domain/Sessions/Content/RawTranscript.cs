namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class RawTranscript
{
    private static readonly JsonSerializerOptions PlainCharacters = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// REC-27. A chunk that does not join adds each of its paragraphs after the rest.
    /// A join goes on in the last block of Raw only when that block is a paragraph, so a heading or a list that the person
    /// made gets a new paragraph after it.
    /// </summary>
    public static string Append(string raw, ChunkParagraphs chunk)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DocumentOf(new JsonArray([.. chunk.Paragraphs.Select(ParagraphOf)]));

        string? appended = null;
        if (ProseMirrorNode.TryRead(raw, document => appended = AppendTo(document, chunk)))
            return appended!;

        var first = chunk.JoinsLastParagraph ? raw + SpaceAfter(raw) + chunk.Paragraphs[0] : $"{raw}\n\n{chunk.Paragraphs[0]}";
        return first + string.Concat(chunk.Paragraphs.Skip(1).Select(words => $"\n\n{words}"));
    }

    private static string AppendTo(JsonObject document, ChunkParagraphs chunk)
    {
        if (document["content"] is not JsonArray content)
            document["content"] = content = [];

        for (var i = 0; i < chunk.Paragraphs.Count; i++)
        {
            if (i == 0 && chunk.JoinsLastParagraph && content.Count > 0 && content[^1] is JsonObject last && ProseMirrorNode.TypeOf(last) == "paragraph")
            {
                JoinInto(last, chunk.Paragraphs[i]);
                continue;
            }

            if (IsEmptyParagraph(content))
                content.Clear();
            content.Add(ParagraphOf(chunk.Paragraphs[i]));
        }

        return document.ToJsonString(PlainCharacters);
    }

    private static void JoinInto(JsonObject paragraph, string words)
    {
        if (paragraph["content"] is not JsonArray { Count: > 0 } inline)
        {
            paragraph["content"] = new JsonArray(TextOf(words));
            return;
        }

        var lastText = inline[^1] is JsonObject last && ProseMirrorNode.TypeOf(last) == "text" ? ProseMirrorNode.StringOf(last["text"]) : null;
        if (lastText is not null && inline[^1]!["marks"] is null or JsonArray { Count: 0 })
            inline[^1]!["text"] = lastText + SpaceAfter(lastText) + words;
        else
            inline.Add(TextOf((lastText is null ? " " : SpaceAfter(lastText)) + words));
    }

    private static string SpaceAfter(string text) => text.Length == 0 || char.IsWhiteSpace(text[^1]) ? "" : " ";

    // The editor keeps one empty paragraph in a document with no words.
    private static bool IsEmptyParagraph(JsonArray content)
        => content is [JsonObject only] && ProseMirrorNode.TypeOf(only) == "paragraph" && only["content"] is null or JsonArray { Count: 0 };

    private static string DocumentOf(JsonArray content)
        => new JsonObject { ["type"] = "doc", ["content"] = content }.ToJsonString(PlainCharacters);

    private static JsonObject ParagraphOf(string words) => new()
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(TextOf(words)),
    };

    private static JsonObject TextOf(string words) => new() { ["type"] = "text", ["text"] = words };
}
