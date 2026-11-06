namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ProseMirrorNode
{
    private const string DocumentType = "doc";

    /// <summary>
    /// Runs <paramref name="read"/> on a ProseMirror document. False when the words are not one, so a caller keeps them as
    /// they are. JsonNode reads lazily, so a duplicate key throws during the read and not during the parse.
    /// </summary>
    public static bool TryRead(string? words, Action<JsonObject> read)
    {
        if (string.IsNullOrWhiteSpace(words))
            return false;

        try
        {
            if (JsonNode.Parse(words) is not JsonObject document || TypeOf(document) != DocumentType)
                return false;

            read(document);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static string? TypeOf(JsonObject element) => StringOf(element["type"]);

    public static string? StringOf(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (InvalidOperationException)
        {
            // A client that cuts an emoji in half sends one half of a surrogate pair, and no string can hold that.
            return null;
        }
    }
}
