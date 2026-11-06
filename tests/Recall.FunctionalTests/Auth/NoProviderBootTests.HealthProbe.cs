namespace Recall.FunctionalTests.Auth;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shouldly;

public partial class NoProviderBootTests
{
    private sealed partial class ServerProcess
    {
        public static async Task<ServerProcess> StartWithHostAsync(string host)
        {
            var server = await LaunchAsync(null, new Dictionary<string, string> { ["AllowedHosts"] = host });
            server.Client.DefaultRequestHeaders.Host = host;
            await server.WaitUntilUpAsync();
            return server;
        }
    }

    [Test]
    public async Task the_probe_passes_on_a_server_with_allowed_hosts_that_refuse_localhost()
    {
        // Arrange
        await using var server = await ServerProcess.StartWithHostAsync("recall.example.com");
        using var localhost = new HttpRequestMessage(HttpMethod.Get, "/health");
        localhost.Headers.Host = "localhost";

        // Act
        using var refused = await server.Client.SendAsync(localhost);
        var probe = await RunProbeAsync(server.Client.BaseAddress!.Port);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        probe.ExitCode.ShouldBe(0);
        probe.Output.ShouldBe("Healthy");
    }

    [Test]
    public async Task the_probe_fails_on_a_200_with_the_body_degraded_from_a_file_store_that_cannot_write()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();
        var files = Path.Combine(server.DataDirectory, "files");
        File.SetUnixFileMode(Path.Combine(files, ".tmp"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        // Act
        using var health = await server.Client.GetAsync("/health");
        var probe = await RunProbeAsync(server.Client.BaseAddress!.Port);

        // Assert
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        probe.ExitCode.ShouldBe(1);
        probe.Output.ShouldBe("Degraded");

        File.SetUnixFileMode(Path.Combine(files, ".tmp"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task the_probe_asks_again_when_a_disk_wakes_and_the_second_answer_decides()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var answers = AnswerAsync(listener, ["Degraded", "Healthy"]);

        // Act
        var probe = await RunProbeAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        await answers;

        // Assert
        probe.ExitCode.ShouldBe(0);
        probe.Output.ShouldBe("Healthy");
    }

    [Test]
    public async Task the_probe_prints_each_line_of_the_answer_and_fails()
    {
        // Arrange
        const string answer = "Unhealthy\nThe background jobs stopped.\nThe key ring changed.";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var answers = AnswerAsync(listener, [answer, answer, answer, answer]);

        // Act
        var probe = await RunProbeAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        await answers;

        // Assert
        probe.ExitCode.ShouldBe(1);
        probe.Output.ShouldBe(answer);
    }

    [Test]
    public async Task the_probe_fails_with_no_server()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // Act
        var probe = await RunProbeAsync(freePort);

        // Assert
        probe.ExitCode.ShouldBe(1);
        probe.Output.ShouldBe("No answer from /health");
    }

    private static async Task AnswerAsync(TcpListener listener, string[] bodies)
    {
        foreach (var body in bodies)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            (await stream.ReadAsync(new byte[1024])).ShouldBePositive();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n{body}"));
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProbeAsync(int port)
    {
        var probe = Path.Combine(TestBuild.RepositoryRoot(), "deploy", "healthcheck.sh");
        var start = new ProcessStartInfo(probe, [port.ToString(System.Globalization.CultureInfo.InvariantCulture), "0"]) { RedirectStandardOutput = true };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }
}
