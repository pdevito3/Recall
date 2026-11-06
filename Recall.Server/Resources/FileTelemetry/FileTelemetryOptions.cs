namespace Recall.Server.Resources.FileTelemetry;

public sealed record RollingFileLimits
{
    public long FileSizeBytes { get; init; }

    public int FilesKept { get; init; }

    public int DaysKept { get; init; }

    public IEnumerable<(string Name, long Value, long Least)> Rules(string signal) =>
    [
        ($"{signal}:{nameof(FileSizeBytes)}", FileSizeBytes, 1),
        ($"{signal}:{nameof(FilesKept)}", FilesKept, 1),
        ($"{signal}:{nameof(DaysKept)}", DaysKept, 0),
    ];
}

public sealed record FileTelemetryOptions
{
    public const string SectionName = "FileTelemetry";

    public RollingFileLimits Logs { get; init; } = default!;

    public RollingFileLimits Traces { get; init; } = default!;

    public RollingFileLimits Metrics { get; init; } = default!;

    public int MetricExportSeconds { get; init; }

    public string? Refusal()
    {
        (string Name, long Value, long Least)[] rules =
        [
            .. Logs.Rules(nameof(Logs)),
            .. Traces.Rules(nameof(Traces)),
            .. Metrics.Rules(nameof(Metrics)),
            (nameof(MetricExportSeconds), MetricExportSeconds, 1),
        ];

        return rules.Where(rule => rule.Value < rule.Least).Select(rule =>
                $"The setting {SectionName}:{rule.Name} is {rule.Value}, and its least value is {rule.Least}. Correct it, and start the server again.")
            .FirstOrDefault();
    }
}

public sealed record FileTelemetryPlan(string LogsDirectory, string TelemetryDirectory, FileTelemetryOptions Options)
{
    public static FileTelemetryPlan? For(IConfiguration configuration)
    {
        if (OwnerOnlyFiles.LogsDirectory(configuration) is not { } logs
            || OwnerOnlyFiles.TelemetryDirectory(configuration) is not { } telemetry
            || configuration.GetSection(FileTelemetryOptions.SectionName).Get<FileTelemetryOptions>() is not { } options)
            return null;

        return new(logs, telemetry, options);
    }

    public string LogFilePath => Path.Combine(LogsDirectory, "recall-.jsonl");

    public JsonLinesFile NewTracesFile() => new(Path.Combine(TelemetryDirectory, "traces-.jsonl"), Options.Traces);

    public JsonLinesFile NewMetricsFile() => new(Path.Combine(TelemetryDirectory, "metrics-.jsonl"), Options.Metrics);
}
