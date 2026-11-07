namespace Recall.Server.Domain.Sessions.Content;

using System.Text;
using System.Text.Json;

/// <summary>
/// The addresses that a link of a document cannot hold: a browser runs or shows each of these schemes as a page of its own. Any
/// other scheme stays, because a save must not refuse the words of a person for an ftp: or an sms: link. A browser removes a tab,
/// a line end, and leading control characters before it reads the scheme, so this does too.
/// </summary>
public static class LinkAddress
{
    private static readonly string[] RefusedSchemes = ["javascript", "vbscript", "data", "blob", "filesystem", "file"];

    public static bool IsAllowed(string? address)
    {
        if (address is null)
            return true;

        var plain = new string(address.Where(c => !char.IsControl(c)).ToArray()).TrimStart();
        var colon = plain.IndexOf(':');
        return colon < 0 || !RefusedSchemes.Contains(plain[..colon], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a document holds an address that a page could read as a refused link. This reads each token, because a page keeps
    /// the last of two equal keys, and a tree of the document has no room for both. Words that are not a document hold no link.
    /// </summary>
    public static bool AnyRefusedIn(string? words)
    {
        if (string.IsNullOrWhiteSpace(words))
            return false;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(words), new JsonReaderOptions { MaxDepth = int.MaxValue });
            var isDocument = false;
            var refused = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                var isType = reader.CurrentDepth == 1 && reader.ValueTextEquals("type");
                var isAddress = reader.ValueTextEquals("href");
                if (!isType && !isAddress)
                    continue;

                reader.Read();
                isDocument |= isType && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("doc");
                refused |= isAddress && reader.TokenType != JsonTokenType.Null && (reader.TokenType != JsonTokenType.String || !IsAllowed(reader.GetString()));
            }

            return isDocument && refused;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // An address with a broken escape, which a page can still read.
            return true;
        }
    }
}
