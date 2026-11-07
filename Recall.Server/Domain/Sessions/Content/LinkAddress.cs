namespace Recall.Server.Domain.Sessions.Content;

using System.Text.Json.Nodes;

/// <summary>
/// The addresses that a link of a document can hold. A browser runs <c>javascript:</c> and shows <c>data:</c> as a page, so only
/// these schemes and an address with no scheme can be a link. A browser removes a tab, a line end, and leading control
/// characters before it reads the scheme, so this does too.
/// </summary>
public static class LinkAddress
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto", "tel"];

    public static bool IsAllowed(string? address)
    {
        if (address is null)
            return true;

        var plain = new string(address.Where(c => !char.IsControl(c)).ToArray()).TrimStart();
        var colon = plain.IndexOf(':');
        if (colon < 0)
            return true;

        // A "/", "?" or "#" before the colon means that the colon is not the end of a scheme.
        var head = plain[..colon];
        return head.IndexOfAny(['/', '?', '#']) >= 0 || AllowedSchemes.Contains(head, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when a document holds a link that <see cref="IsAllowed"/> refuses. Words that are not a document hold no link.</summary>
    public static bool AnyRefusedIn(string? words)
    {
        var refused = false;
        ProseMirrorNode.TryRead(words, document => refused = HoldsRefused(document));
        return refused;
    }

    private static bool HoldsRefused(JsonObject node)
    {
        if (node["marks"] is JsonArray marks && marks.OfType<JsonObject>().Any(mark => !IsAllowed(ProseMirrorNode.StringOf(mark["attrs"]?["href"]))))
            return true;

        return node["content"] is JsonArray children && children.OfType<JsonObject>().Any(HoldsRefused);
    }
}
