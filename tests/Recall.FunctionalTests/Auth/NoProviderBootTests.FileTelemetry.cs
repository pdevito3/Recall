namespace Recall.FunctionalTests.Auth;

using System.Text.Json;
using Recall.Server.Resources.FileTelemetry;
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
    public async Task a_negative_days_kept_stops_the_boot_with_one_line_and_no_stack_trace()
    {
        // Arrange
        await using var server = await ServerProcess.LaunchAsync(null, new Dictionary<string, string>
        {
            ["FileTelemetry__Logs__DaysKept"] = "-1",
        });

        // Act
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldBe(1);
        server.Output.ShouldContain("The setting FileTelemetry:Logs:DaysKept is -1");
        server.Output.ShouldNotContain("   at ");
    }

    [Test]
    public async Task the_default_limits_keep_a_metric_file_for_each_of_14_days()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
        var telemetry = Path.Combine(root, "data", "telemetry");
        Directory.CreateDirectory(telemetry);
        var today = TimeProvider.System.GetLocalNow().Date;
        string FileOf(int daysAgo) => $"metrics-{today.AddDays(-daysAgo):yyyyMMdd}.jsonl";
        for (var daysAgo = 1; daysAgo <= 20; daysAgo++)
            await File.WriteAllTextAsync(Path.Combine(telemetry, FileOf(daysAgo)), "{}\n");

        // Act
        await using var server = await ServerProcess.StartAsync(root, new Dictionary<string, string>
        {
            ["FileTelemetry__MetricExportSeconds"] = "1",
        });
        await FirstLineAsync(telemetry, FileOf(0), _ => true);

        // Assert
        Directory.GetFiles(telemetry, "metrics-*").Select(Path.GetFileName).Order().ShouldBe(Enumerable.Range(0, 14).Select(FileOf).Order());
    }

    [Test]
    public async Task a_file_that_cannot_roll_gives_one_warning_for_each_file_on_the_console_and_the_console_keeps_each_log()
    {
        // Arrange
        const int requests = 40;
        await using var server = await ServerProcess.StartAsync(settings: new Dictionary<string, string>
        {
            ["FileTelemetry__Logs__FileSizeBytes"] = "1024",
            ["FileTelemetry__Traces__FileSizeBytes"] = "1024",
        });
        var logs = Path.Combine(server.DataDirectory, "logs");
        var telemetry = Path.Combine(server.DataDirectory, "telemetry");
        var path = $"/{Guid.NewGuid():N}.txt";
        using var first = await server.Client.GetAsync($"/{Guid.NewGuid():N}.txt");
        await FirstLineAsync(logs, "recall-*.jsonl", _ => true);
        await FirstLineAsync(telemetry, "traces-*.jsonl", _ => true);
        File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(telemetry, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            // Act
            for (var number = 0; number < requests; number++)
                (await server.Client.GetAsync(path)).Dispose();

            // Assert
            var lines = server.Output.Split('\n');
            string[] WarningsIn(string[] console) => [.. console.Where(line => line.Contains(FileSinkFailureNotice.Text, StringComparison.Ordinal))];
            for (var attempt = 0; attempt < 150 && (lines.Count(line => line.Contains(path, StringComparison.Ordinal)) < requests || WarningsIn(lines).Length < 2); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                lines = server.Output.Split('\n');
            }

            lines.Count(line => line.Contains(path, StringComparison.Ordinal)).ShouldBe(requests);
            var warnings = WarningsIn(lines);
            warnings.Length.ShouldBe(2);
            warnings.ShouldContain(line => line.Contains(logs, StringComparison.Ordinal));
            warnings.ShouldContain(line => line.Contains(telemetry, StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(telemetry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
