namespace Recall.Server.Domain.Sessions.Content;

using System.Text;
using System.Text.Json.Nodes;

public static class ProseMirrorToPlainText
{
    internal const string StrikeSign = "~~";

    public static string Render(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var blocks = new List<string>();
        return ProseMirrorNode.TryRead(json, document => RenderNode(document, blocks))
            ? string.Join('\n', blocks).Trim()
            : json;
    }

    private static void RenderNode(JsonNode? node, List<string> blocks)
    {
        if (node is not JsonObject element)
            return;

        switch (ProseMirrorNode.TypeOf(element))
        {
            case "paragraph" or "heading" or "codeBlock":
                var line = new StringBuilder();
                RenderInline(element["content"], line);
                blocks.Add(line.ToString());
                break;
            case "horizontalRule":
                break;
            default:
                if (element["content"] is JsonArray children)
                {
                    foreach (var child in children)
                        RenderNode(child, blocks);
                }

                break;
        }
    }

    private static void RenderInline(JsonNode? content, StringBuilder line)
    {
        if (content is not JsonArray children)
            return;

        var inlines = children.OfType<JsonObject>().ToList();
        var strikes = ProseMirrorNode.StrikeEdgesOf(inlines);
        for (var at = 0; at < inlines.Count; at++)
        {
            var child = inlines[at];
            if (strikes[at].Opens)
                line.Append(StrikeSign);

            switch (ProseMirrorNode.TypeOf(child))
            {
                case "text":
                    line.Append(ProseMirrorNode.StringOf(child["text"]));
                    break;
                case "mention":
                    line.Append(ProseMirrorNode.StringOf((child["attrs"] as JsonObject)?["label"]));
                    break;
                default:
                    RenderInline(child["content"], line);
                    break;
            }

            if (strikes[at].Closes)
                line.Append(StrikeSign);
        }
    }
}
