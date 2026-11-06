namespace Recall.Server.Resources.FileTelemetry;

using Entrypoints.Http.OpenApi;

public sealed record RollingFileLimits
{
    public long FileSizeBytes { get; init; }

    public int FilesKept { get; init; }

    public int DaysKept { get; init; }
}

public sealed record FileTelemetryOptions
{
    public const string SectionName = "FileTelemetry";

    public RollingFileLimits Logs { get; init; } = default!;

    public RollingFileLimits Traces { get; init; } = default!;

    public RollingFileLimits Metrics { get; init; } = default!;

    public int MetricExportSeconds { get; init; }
}

/// <summary>
/// Where the log and telemetry files of this process go. The folders are beside the database, so that a process run
/// and an image run keep them in the data directory with no other setting.
/// </summary>
public sealed record FileTelemetryPlan(string LogsDirectory, string TelemetryDirectory, FileTelemetryOptions Options)
{
    /// <summary>
    /// Null when the process keeps no files: an in-memory database, a build that writes the OpenAPI document, or no
    /// configuration file.
    /// </summary>
    public static FileTelemetryPlan? For(IConfiguration configuration)
    {
        if (OpenApiBuildTime.IsActive
            || OwnerOnlyFiles.LogsDirectory(configuration) is not { } logs
            || OwnerOnlyFiles.TelemetryDirectory(configuration) is not { } telemetry
            || configuration.GetSection(FileTelemetryOptions.SectionName).Get<FileTelemetryOptions>() is not { } options)
            return null;

        return new(logs, telemetry, options);
    }

    public string LogFilePath => Path.Combine(LogsDirectory, "recall-.jsonl");

    public JsonLinesFile NewTracesFile() => new(Path.Combine(TelemetryDirectory, "traces-.jsonl"), Options.Traces);

    public JsonLinesFile NewMetricsFile() => new(Path.Combine(TelemetryDirectory, "metrics-.jsonl"), Options.Metrics);
}
