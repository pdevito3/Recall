namespace Recall.Server.Resources.FileTelemetry;

public sealed record RollingFileLimits
{
    // Ten years. A much larger value passes the range of a date, and the server then fails when a file rolls.
    private const int MostDaysKept = 3650;

    public long FileSizeBytes { get; init; }

    public int FilesKept { get; init; }

    public int DaysKept { get; init; }

    public IEnumerable<(string Name, long Value, long Least, long Most)> Rules(string signal) =>
    [
        ($"{signal}:{nameof(FileSizeBytes)}", FileSizeBytes, 1, long.MaxValue),
        ($"{signal}:{nameof(FilesKept)}", FilesKept, 1, int.MaxValue),
        ($"{signal}:{nameof(DaysKept)}", DaysKept, 0, MostDaysKept),
    ];
}

public sealed record FileTelemetryOptions
{
    public const string SectionName = "FileTelemetry";

    // One day. The value becomes milliseconds in an int, and this keeps it far from an overflow.
    private const int MostMetricExportSeconds = 86_400;

    public RollingFileLimits Logs { get; init; } = default!;

    public RollingFileLimits Traces { get; init; } = default!;

    public RollingFileLimits Metrics { get; init; } = default!;

    public int MetricExportSeconds { get; init; }

    public string? Refusal()
    {
        (string Name, long Value, long Least, long Most)[] rules =
        [
            .. Logs.Rules(nameof(Logs)),
            .. Traces.Rules(nameof(Traces)),
            .. Metrics.Rules(nameof(Metrics)),
            (nameof(MetricExportSeconds), MetricExportSeconds, 1, MostMetricExportSeconds),
        ];

        return rules.Where(rule => rule.Value < rule.Least || rule.Value > rule.Most).Select(rule =>
                rule.Value < rule.Least
                    ? $"The setting {SectionName}:{rule.Name} is {rule.Value}, and its least value is {rule.Least}. Correct it, and start the server again."
                    : $"The setting {SectionName}:{rule.Name} is {rule.Value}, and its largest value is {rule.Most}. Correct it, and start the server again.")
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

    /// <summary>The refusal of a limit that is out of its range, or that is not a whole number in the range of its type.</summary>
    public static string? RefusalFor(IConfiguration configuration)
    {
        try
        {
            return For(configuration)?.Options.Refusal();
        }
        catch (InvalidOperationException notANumber)
        {
            return $"{notANumber.Message} Correct it, and start the server again.";
        }
    }

    public string LogFilePath => Path.Combine(LogsDirectory, "recall-.jsonl");

    public JsonLinesFile NewTracesFile() => new(Path.Combine(TelemetryDirectory, "traces-.jsonl"), Options.Traces);

    public JsonLinesFile NewMetricsFile() => new(Path.Combine(TelemetryDirectory, "metrics-.jsonl"), Options.Metrics);
}
