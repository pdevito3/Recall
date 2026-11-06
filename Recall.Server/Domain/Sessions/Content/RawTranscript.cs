namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class RawTranscript
{
    private static readonly JsonSerializerOptions PlainCharacters = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Append(string raw, string words)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DocumentOf(new JsonArray(ParagraphOf(words)));

        string? appended = null;
        if (ProseMirrorNode.TryRead(raw, document => appended = AppendTo(document, words)))
            return appended!;

        return $"{raw}\n\n{words}";
    }

    private static string AppendTo(JsonObject document, string words)
    {
        if (document["content"] is JsonArray content)
        {
            if (IsEmptyParagraph(content))
                content.Clear();
            content.Add(ParagraphOf(words));
        }
        else
        {
            document["content"] = new JsonArray(ParagraphOf(words));
        }

        return document.ToJsonString(PlainCharacters);
    }

    // The editor keeps one empty paragraph in a document with no words.
    private static bool IsEmptyParagraph(JsonArray content)
        => content is [JsonObject only] && ProseMirrorNode.TypeOf(only) == "paragraph" && only["content"] is null or JsonArray { Count: 0 };

    private static string DocumentOf(JsonArray content)
        => new JsonObject { ["type"] = "doc", ["content"] = content }.ToJsonString(PlainCharacters);

    private static JsonObject ParagraphOf(string words) => new()
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = words }),
    };
}
