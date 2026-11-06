namespace Recall.Server.Domain.Support;

using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Resources;
using Resources.FileTelemetry;
using Serilog;

/// <summary>The zip goes straight to the caller, so that a full disk does not stop the download that shows why the disk is full.</summary>
public sealed class SupportBundle(FileTelemetryPlan? files, IEnumerable<KeyValuePair<string, string?>> settings, string environment, DateTimeOffset madeAt)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILogger _logger = Log.ForContext<SupportBundle>();

    public string FileName => $"recall-support-{madeAt:yyyyMMdd-HHmmss}.zip";

    public async Task WriteToAsync(Stream output, CancellationToken cancellationToken)
    {
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken);
        await AddJsonAsync(zip, "settings.json", RedactedSettings.Of(settings), cancellationToken);

        var skipped = new List<string>();
        if (files is not null)
        {
            await AddFolderAsync(zip, "logs", files.LogsDirectory, skipped, cancellationToken);
            await AddFolderAsync(zip, "telemetry", files.TelemetryDirectory, skipped, cancellationToken);
        }

        await AddJsonAsync(zip, "version.json", new
        {
            Version = typeof(SupportBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Environment = environment,
            MadeAt = madeAt,
            SkippedFiles = skipped,
        }, cancellationToken);
    }

    private static async Task AddJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken);
        await JsonSerializer.SerializeAsync(entry, value, Indented, cancellationToken);
    }

    private async Task AddFolderAsync(ZipArchive zip, string folderName, string directory, List<string> skipped, CancellationToken cancellationToken)
    {
        foreach (var file in RegularFilesIn(directory, folderName, skipped))
        {
            var name = $"{folderName}/{Path.GetFileName(file)}";
            try
            {
                await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);
                await using var entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken);
                await source.CopyToAsync(entry, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Skip(name, exception, skipped);
            }
        }
    }

    private string[] RegularFilesIn(string directory, string folderName, List<string> skipped)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.GetFiles(directory).Where(file => new FileInfo(file).LinkTarget is null).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Skip($"{folderName}/", exception, skipped);
            return [];
        }
    }

    private void Skip(string name, Exception exception, List<string> skipped)
    {
        _logger.Here().Warning(exception, "The support bundle skipped {Name}", name);
        skipped.Add(name);
    }
}
