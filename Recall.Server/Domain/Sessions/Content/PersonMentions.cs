namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class PersonMentions
{
    // The default encoder writes each non-ASCII character as an escape, so a rename or a tag would make the words longer on disk.
    internal static readonly JsonSerializerOptions KeepWords = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    /// <summary>Turns each mention of the Person into its label as plain words, with the marks of the mention.</summary>
    public static string Unlink(string json, string personId)
    {
        var unlinked = json;
        ProseMirrorNode.TryRead(json, document =>
        {
            if (Unlink(document, personId))
                unlinked = document.ToJsonString(KeepWords);
        });
        return unlinked;
    }

    private static bool Unlink(JsonObject element, string personId)
    {
        if (element["content"] is not JsonArray children)
            return false;

        var changed = false;
        for (var at = children.Count - 1; at >= 0; at--)
        {
            if (children[at] is not JsonObject child)
                continue;

            if (ProseMirrorNode.TypeOf(child) == "mention" && child["attrs"] is JsonObject attrs && ProseMirrorNode.StringOf(attrs["personId"]) == personId)
            {
                children.RemoveAt(at);
                if (WordsOf(child, attrs) is { } words)
                    children.Insert(at, words);
                changed = true;
            }
            else
            {
                changed |= Unlink(child, personId);
            }
        }

        if (changed)
            JoinWords(children);
        return changed;
    }

    // ProseMirror allows no empty text node, so a mention with no label leaves nothing.
    private static JsonObject? WordsOf(JsonObject mention, JsonObject attrs)
    {
        if (attrs["label"] is not JsonValue label || label.GetValueKind() != JsonValueKind.String || ProseMirrorNode.StringOf(label) == string.Empty)
            return null;

        var words = new JsonObject { ["type"] = "text", ["text"] = label.DeepClone() };
        if (mention["marks"] is { } marks)
            words["marks"] = marks.DeepClone();
        return words;
    }

    // The editor joins two text nodes with the same marks, so the words read as if they never had a tag. A later tag of a
    // name across the join needs one text node.
    private static void JoinWords(JsonArray children)
    {
        for (var at = children.Count - 1; at > 0; at--)
        {
            if (children[at] is not JsonObject after || children[at - 1] is not JsonObject before
                || ProseMirrorNode.TypeOf(after) != "text" || ProseMirrorNode.TypeOf(before) != "text"
                || !JsonNode.DeepEquals(before["marks"], after["marks"])
                || ProseMirrorNode.StringOf(before["text"]) is not { } first || ProseMirrorNode.StringOf(after["text"]) is not { } second)
                continue;

            before["text"] = first + second;
            children.RemoveAt(at);
        }
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
