namespace Recall.Server.Domain.Support;

using System.Text.RegularExpressions;

/// <summary>
/// The settings for a support bundle. A setting shows its name, and only a setting on this list shows its value. A new
/// setting thus starts hidden, so a secret that someone adds later stays out of a bundle.
/// </summary>
public static partial class RedactedSettings
{
    public const string Hidden = "[redacted]";

    public static SortedDictionary<string, string> Of(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var shown = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            if (value is not null)
                shown[key] = SafeToShow().IsMatch(key) ? value : Hidden;
        }

        return shown;
    }

    [GeneratedRegex(
        """
        ^(AllowedHosts
        |ASPNETCORE_ENVIRONMENT
        |Auth:OpenSignup
        |Auth:Providers:\d+:(Id|DisplayName|Issuer|ClientId|AllowHttp)
        |FileStore:Root
        |FileTelemetry:.+
        |Kestrel:Endpoints:[^:]+:Url
        |Serilog:MinimumLevel(:.+)?
        )$
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex SafeToShow();
}
