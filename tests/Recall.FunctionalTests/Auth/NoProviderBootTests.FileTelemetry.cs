namespace Recall.FunctionalTests.Auth;

using System.Text.Json;
using Shouldly;

public partial class NoProviderBootTests
{
    private static async Task<JsonElement> FirstLineAsync(string folder, string pattern, Func<JsonElement, bool> match)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, pattern))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        var json = JsonDocument.Parse(line).RootElement;
                        if (match(json))
                            return json;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new InvalidOperationException($"No line in {folder}/{pattern} matched in 30 seconds.");
    }

    [Test]
    public async Task a_request_writes_a_json_log_line_and_a_span_with_one_trace_id_to_owner_only_files()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync(settings: new Dictionary<string, string>
        {
            ["FileTelemetry__MetricExportSeconds"] = "1",
        });
        var logs = Path.Combine(server.DataDirectory, "logs");
        var telemetry = Path.Combine(server.DataDirectory, "telemetry");
        var path = $"/{Guid.NewGuid():N}.txt";

        // Act
        using var response = await server.Client.GetAsync(path);

        // Assert
        var log = await FirstLineAsync(logs, "recall-*.jsonl", line =>
            line.TryGetProperty("RequestPath", out var requestPath) && requestPath.GetString() == path);
        var traceId = log.GetProperty("@tr").GetString();
        traceId.ShouldNotBeNullOrEmpty();
        var span = await FirstLineAsync(telemetry, "traces-*.jsonl", line => line.GetProperty("traceId").GetString() == traceId);
        span.GetProperty("kind").GetString().ShouldBe("Server");

        await FirstLineAsync(telemetry, "metrics-*.jsonl", line => line.GetProperty("name").GetString() == "http.server.request.duration");

        File.GetUnixFileMode(logs).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(telemetry).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(Directory.GetFiles(logs).First()).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Directory.GetFiles(telemetry).First()).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
