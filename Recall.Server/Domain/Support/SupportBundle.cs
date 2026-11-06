namespace Recall.Server.Domain.Support;

using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Resources;
using Resources.FileTelemetry;
using Serilog;

/// <summary>
/// The one zip that an Owner sends for support: the log and telemetry files, the version, and the settings with the
/// secrets hidden. It goes straight to the caller, so that a full disk does not stop the download that shows why the
/// disk is full.
/// </summary>
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

    /// <summary>
    /// Takes each regular file of the folder and no link. The server writes to these files during the copy, and a roll can
    /// remove one, so a file that cannot open goes in the list of the skipped files.
    /// </summary>
    private async Task AddFolderAsync(ZipArchive zip, string folderName, string directory, List<string> skipped, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory).Where(file => new FileInfo(file).LinkTarget is null))
        {
            var name = $"{folderName}/{Path.GetFileName(file)}";
            FileStream source;
            try
            {
                source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Here().Warning(exception, "The support bundle skipped the file {Name}", name);
                skipped.Add(name);
                continue;
            }

            await using (source)
            {
                await using var entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(cancellationToken);
                await source.CopyToAsync(entry, cancellationToken);
            }
        }
    }
}
