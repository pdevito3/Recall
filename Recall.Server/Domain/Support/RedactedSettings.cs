namespace Recall.Server.Domain.Support;

using System.Text.RegularExpressions;

public static partial class RedactedSettings
{
    public const string Hidden = "[redacted]";

    public static SortedDictionary<string, string> Of(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var shown = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            if (value is not null)
                shown[key] = AllowlistOfShownValues().IsMatch(key) ? value : Hidden;
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
    private static partial Regex AllowlistOfShownValues();
}
