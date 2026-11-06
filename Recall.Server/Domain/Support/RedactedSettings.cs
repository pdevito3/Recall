namespace Recall.Server.Domain.Support;

using System.Text.RegularExpressions;

public static partial class RedactedSettings
{
    public const string Hidden = "[redacted]";

    // The process also has the variables of its host, such as a token of another tool. Recall reads none of them.
    private static readonly string[] SectionsThatRecallReads =
    [
        "AllowedHosts",
        "ASPNETCORE_ENVIRONMENT",
        "Auth",
        "ConnectionStrings",
        "FileStore",
        "FileTelemetry",
        "Kestrel",
        "Mcp",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
        "Serilog",
        "Swagger",
        "Tenancy",
    ];

    public static SortedDictionary<string, string> Of(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var shown = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            if (value is not null && IsInASectionThatRecallReads(key))
                shown[key] = AllowlistOfShownValues().IsMatch(key) ? value : Hidden;
        }

        return shown;
    }

    private static bool IsInASectionThatRecallReads(string key) => SectionsThatRecallReads.Any(section =>
        key.Equals(section, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(
        """
        ^(AllowedHosts
        |ASPNETCORE_ENVIRONMENT
        |Auth:OpenSignup
        |Auth:Providers:\d+:(Id|DisplayName|Issuer|ClientId|AllowHttp)
        |FileStore:Root
        |FileTelemetry:(Logs|Traces|Metrics):(FileSizeBytes|FilesKept|DaysKept)
        |FileTelemetry:MetricExportSeconds
        |Kestrel:Endpoints:[^:]+:Url
        |Serilog:MinimumLevel(:.+)?
        )$
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex AllowlistOfShownValues();
}
