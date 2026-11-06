namespace Recall.UnitTests.Domain.Support;

using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using Recall.Server.Domain.Support;
using Recall.Server.Resources.FileTelemetry;
using Shouldly;

[UnsupportedOSPlatform("windows")]
public class SupportBundleTests
{
    [Test]
    public async Task a_folder_that_the_server_cannot_read_goes_in_the_skipped_list_and_the_rest_of_the_zip_still_arrives()
    {
        // Arrange
        var data = Directory.CreateTempSubdirectory("recall-bundle-").FullName;
        var logs = Directory.CreateDirectory(Path.Combine(data, "logs")).FullName;
        var telemetry = Directory.CreateDirectory(Path.Combine(data, "telemetry")).FullName;
        await File.WriteAllTextAsync(Path.Combine(logs, "recall-1.jsonl"), "{}");
        await File.WriteAllTextAsync(Path.Combine(telemetry, "traces-1.jsonl"), "{}");
        File.SetUnixFileMode(logs, UnixFileMode.None);
        var bundle = new SupportBundle(new FileTelemetryPlan(logs, telemetry, new FileTelemetryOptions()), [], "Production", DateTimeOffset.UnixEpoch);
        using var output = new MemoryStream();

        try
        {
            // Act
            await bundle.WriteToAsync(output, CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(data, recursive: true);
        }

        // Assert
        output.Position = 0;
        await using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        zip.Entries.Select(entry => entry.FullName).ShouldBe(["settings.json", "telemetry/traces-1.jsonl", "version.json"]);
        await using var version = await zip.GetEntry("version.json")!.OpenAsync();
        var skipped = JsonDocument.Parse(version).RootElement.GetProperty("SkippedFiles").EnumerateArray().Select(item => item.GetString());
        skipped.ShouldBe(["logs/"]);
    }
}
