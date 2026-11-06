namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class PersonMentions
{
    // The default encoder writes each non-ASCII character as an escape, so a rename would make the words longer on disk.
    private static readonly JsonSerializerOptions KeepWords = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlySet<string> PersonIdsIn(string? json)
    {
        var personIds = new HashSet<string>(StringComparer.Ordinal);
        ProseMirrorNode.TryRead(json, document => Walk(document, mention =>
        {
            if (ProseMirrorNode.StringOf(mention["personId"]) is { Length: > 0 } personId)
                personIds.Add(personId);
        }));
        return personIds;
    }

    public static string Relabel(string json, string personId, string label)
    {
        var relabeled = json;
        ProseMirrorNode.TryRead(json, document =>
        {
            var changed = false;
            Walk(document, mention =>
            {
                if (ProseMirrorNode.StringOf(mention["personId"]) != personId
                    || ProseMirrorNode.StringOf(mention["label"]) == label)
                    return;

                mention["label"] = label;
                changed = true;
            });
            if (changed)
                relabeled = document.ToJsonString(KeepWords);
        });
        return relabeled;
    }

    private static void Walk(JsonNode node, Action<JsonObject> onMentionAttrs)
    {
        if (node is not JsonObject element)
            return;

        if (ProseMirrorNode.TypeOf(element) == "mention" && element["attrs"] is JsonObject attrs)
            onMentionAttrs(attrs);

        if (element["content"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonNode>())
                Walk(child, onMentionAttrs);
        }
    }
}
