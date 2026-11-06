namespace Recall.FunctionalTests.Auth;

using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

/// <summary>
/// The real server in its own process, with no setting for sign-in. Only a process shows this: an in-process host shares
/// the environment of the other tests, and the modes of the files come from the umask of a process.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class NoProviderBootTests
{
    private const UnixFileMode WideDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly string[] ClearedPrefixes = ["Auth__", "Mcp__", "Tenancy__", "OTEL_", "ConnectionStrings__", "FileStore__", "ASPNETCORE_", "DOTNET_ENVIRONMENT"];

    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _root;
        private bool _killed;

        private ServerProcess(Process process, string root, HttpClient client)
        {
            _process = process;
            _root = root;
            Client = client;
        }

        public HttpClient Client { get; }

        public string Root => _root;

        public string DataDirectory => Path.Combine(_root, "data");

        public string Database => Path.Combine(DataDirectory, "recall.db");

        public static async Task<ServerProcess> StartAsync(string? existingRoot = null)
        {
            var root = existingRoot ?? Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
            var data = Path.Combine(root, "data");
            var web = Path.Combine(root, "web");
            Directory.CreateDirectory(data);
            // The hosts in this test process apply the umask of the Server, so a mode at create would be narrowed here
            File.SetUnixFileMode(data, WideDirectoryMode);
            Directory.CreateDirectory(web);
            await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<html>recall spa</html>");

            var httpPort = FreePort();
            // The child would inherit the narrow umask of this process, and hide a Server that does not set its own
            var start = new ProcessStartInfo("/bin/sh", ["-c", "umask 022; exec dotnet \"$0\"", ServerDll()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ServerDll())!,
            };
            foreach (var key in start.Environment.Keys.Where(key => ClearedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))).ToList())
                start.Environment.Remove(key);

            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
            start.Environment["ASPNETCORE_WEBROOT"] = web;
            start.Environment["ConnectionStrings__appdb"] = $"Data Source={Path.Combine(data, "recall.db")}";
            start.Environment["FileStore__Root"] = Path.Combine(data, "files");
            start.Environment["Kestrel__Endpoints__http__Url"] = $"http://127.0.0.1:{httpPort}";
            start.Environment["Kestrel__Endpoints__grpc__Url"] = $"http://127.0.0.1:{FreePort()}";

            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            var server = new ServerProcess(process, root, client);
            try
            {
                await server.WaitUntilUpAsync();
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }

            return server;
        }

        private async Task WaitUntilUpAsync()
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                _process.HasExited.ShouldBeFalse("The server stopped during its boot.");
                try
                {
                    using var response = await Client.GetAsync(AuthRoutes.Status);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            throw new InvalidOperationException("The server did not answer in 30 seconds.");
        }

        /// <summary>A SIGKILL, so the last connection runs no checkpoint on its close, and the files stay as a crash leaves them.</summary>
        public async Task KillAsync()
        {
            if (_killed)
                return;

            _killed = true;
            Client.Dispose();
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static string ServerDll()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
                directory = directory.Parent;
            directory.ShouldNotBeNull();
            return Path.Combine(directory.FullName, "Recall.Server", "bin", TestBuild.Configuration, "net10.0", "Recall.Server.dll");
        }
    }

    private static class TestBuild
    {
#if DEBUG
        public const string Configuration = "Debug";
#else
        public const string Configuration = "Release";
#endif
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_restart_after_a_kill_clears_committed_deletes_from_the_wal_before_the_first_request()
    {
        // Arrange
        var words = $"deleted{Guid.NewGuid():N}";
        await using var first = await ServerProcess.StartAsync();
        await CommitAWriteAndADeleteAsync(first.Database, words);
        await first.KillAsync();
        Holds($"{first.Database}-wal", words).ShouldBeTrue("The kill must leave the words in the WAL.");

        // Act
        await using var second = await ServerProcess.StartAsync(first.Root);
        await second.KillAsync();

        // Assert
        Holds($"{second.Database}-wal", words).ShouldBeFalse();
        Holds(second.Database, words).ShouldBeFalse();
    }

    private static bool Holds(string file, string words)
        => File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.UTF8.GetBytes(words)) >= 0;

    private static async Task CommitAWriteAndADeleteAsync(string database, string words)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON; CREATE TABLE kill_test (words TEXT NOT NULL); INSERT INTO kill_test VALUES (@words); DELETE FROM kill_test;";
        command.Parameters.AddWithValue("@words", words);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task with_no_provider_the_server_stays_up_and_reports_no_login_method()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        var status = await server.Client.GetFromJsonAsync<JsonElement>(AuthRoutes.Status);
        using var login = await server.Client.GetAsync(AuthRoutes.Login);
        using var api = await server.Client.GetAsync("/api/v1/tenants");
        using var metadata = await server.Client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var indexPage = await server.Client.GetAsync("/");
        using var spaRoute = await server.Client.GetAsync("/authors");
        using var missingAsset = await server.Client.GetAsync("/assets/x.js");
        using var missingAuth = await server.Client.GetAsync("/auth/nope");

        // Assert
        status.GetProperty("state").GetString().ShouldBe("no_login_method");
        status.GetProperty("setupRequired").GetBoolean().ShouldBeTrue();
        status.GetProperty("message").GetString().ShouldBe(AuthPages.NoMethodMessage);
        status.GetProperty("modes").GetArrayLength().ShouldBe(0);
        status.GetProperty("callbackUrl").GetString().ShouldEndWith(AuthRoutes.Callback);

        login.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await login.Content.ReadAsStringAsync()).ShouldContain(AuthPages.NoMethodMessage);

        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        api.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme).ShouldBe(["Bearer"]);
        metadata.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        indexPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await indexPage.Content.ReadAsStringAsync()).ShouldContain("recall spa");
        spaRoute.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await spaRoute.Content.ReadAsStringAsync()).ShouldContain("recall spa");
        missingAsset.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingAuth.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task the_data_directory_and_the_database_files_are_for_the_owner_only()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        var directoryMode = File.GetUnixFileMode(server.DataDirectory);
        var databaseMode = File.GetUnixFileMode(server.Database);
        var walMode = File.GetUnixFileMode($"{server.Database}-wal");

        // Assert
        directoryMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        databaseMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        walMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
