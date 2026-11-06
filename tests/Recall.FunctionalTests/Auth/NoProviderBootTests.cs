namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

/// <summary>
/// The real server in its own process, with no setting for sign-in. Only a process shows this: an in-process host shares
/// the environment of the other tests, and the modes of the files come from the umask of a process.
/// </summary>
[UnsupportedOSPlatform("windows")]
public partial class NoProviderBootTests
{
    private const UnixFileMode WideDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly string[] ClearedPrefixes = ["Auth__", "Mcp__", "Tenancy__", "OTEL_", "ConnectionStrings__", "FileStore__", "ASPNETCORE_", "DOTNET_ENVIRONMENT"];

    private sealed partial class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output;

        private ServerProcess(Process process, ConcurrentQueue<string> output, string root, HttpClient client)
        {
            _process = process;
            _output = output;
            Root = root;
            Client = client;
        }

        public HttpClient Client { get; }

        public string Root { get; }

        public string DataDirectory => Path.Combine(Root, "data");

        public string Database => Path.Combine(DataDirectory, "recall.db");

        public string KeysDirectory => Path.Combine(DataDirectory, "keys");

        public int ExitCode => _process.ExitCode;

        public bool HasExited => _process.HasExited;

        public string Output => string.Join('\n', _output);

        public static async Task<ServerProcess> StartAsync(string? existingRoot = null, IReadOnlyDictionary<string, string>? settings = null)
        {
            var server = await LaunchAsync(existingRoot, settings ?? new Dictionary<string, string>());
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

        public static async Task<ServerProcess> LaunchAsync(string? existingRoot, IReadOnlyDictionary<string, string> settings)
        {
            var root = existingRoot ?? Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
            var data = Path.Combine(root, "data");
            var web = Path.Combine(root, "web");
            Directory.CreateDirectory(data);
            // The hosts in this test process apply the umask of the Server, so a mode at create would be narrowed here
            File.SetUnixFileMode(data, WideDirectoryMode);
            Directory.CreateDirectory(web);
            await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<html>recall spa</html>");

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
            start.Environment["Kestrel__Endpoints__http__Url"] = "http://127.0.0.1:0";
            start.Environment["Serilog__MinimumLevel__Override__Microsoft"] = "Information";
            foreach (var (key, value) in settings)
                start.Environment[key] = value;

            var process = Process.Start(start)!;
            var output = new ConcurrentQueue<string>();
            process.OutputDataReceived += (_, line) => { if (line.Data is { } text) output.Enqueue(text); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is { } text) output.Enqueue(text); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            return new ServerProcess(process, output, root, client);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public string[] KeyFiles() => [.. Directory.GetFiles(KeysDirectory).Order(StringComparer.Ordinal)];

        public async Task StopAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        private async Task WaitUntilUpAsync()
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                _process.HasExited.ShouldBeFalse($"The server stopped during its boot. Its output: {Output}");
                if (Client.BaseAddress is null)
                {
                    if (ListeningAddress().Match(Output) is { Success: true } address)
                        Client.BaseAddress = new Uri(address.Value);
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                    continue;
                }

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

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await StopAsync();
            _process.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        [GeneratedRegex(@"http://127\.0\.0\.1:[1-9][0-9]*")]
        private static partial Regex ListeningAddress();

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
    public async Task a_data_directory_that_takes_no_write_stops_the_boot_with_one_line_that_does_not_blame_the_migrations()
    {
        // Arrange
        var blocker = Path.Combine(Path.GetTempPath(), $"recall_blocker_{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "");
        var dataDirectory = Path.Combine(blocker, "data");

        try
        {
            // Act
            await using var server = await ServerProcess.LaunchAsync(null, new Dictionary<string, string>
            {
                ["ConnectionStrings__appdb"] = $"Data Source={Path.Combine(dataDirectory, "recall.db")}",
            });
            await server.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

            // Assert
            server.ExitCode.ShouldBe(1);
            var lines = server.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Where(line => line.Contains(dataDirectory, StringComparison.Ordinal)).ShouldHaveSingleItem().ShouldContain("Mount it read-write");
            lines.ShouldNotContain(line => line.Contains("migration", StringComparison.OrdinalIgnoreCase));
            lines.ShouldNotContain(line => line.Contains("Hosting failed", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(blocker);
        }
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
        var setupPage = await login.Content.ReadAsStringAsync();
        Regex.Replace(setupPage, "</?code>", "").ShouldContain(AuthPages.NoMethodMessage);
        setupPage.ShouldContain("<code>Auth__Providers__0__Issuer</code>");
        setupPage.ShouldContain("<code>Auth__Providers__0__ClientId</code>");
        setupPage.ShouldContain("<code>Auth__Providers__0__ClientSecret</code>");
        setupPage.ShouldContain("<code>docs/authentication.md</code>");
        setupPage.ShouldContain("<wbr>/auth<wbr>/oidc<wbr>/callback</code>");
        setupPage.ShouldContain("href=\"/auth/login?returnUrl=%2F\">Try again</a>");

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
    public async Task a_person_who_opens_a_path_with_a_dot_gets_the_spa_and_a_missing_file_stays_a_404()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var page = await GetAsync(server, "/a.b", "text/html,application/xhtml+xml");
        using var doubleSlashPage = await GetAsync(server, "//evil.example", "text/html");
        using var missingScript = await GetAsync(server, "/assets/none.js", "*/*");
        using var noAccept = await GetAsync(server, "/users.csv", null);
        using var reserved = await GetAsync(server, "/auth/a.b", "text/html");
        using var reservedApi = await GetAsync(server, "/api/a.b", "text/html");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("recall spa");
        doubleSlashPage.Headers.Location.ShouldBeNull();
        missingScript.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await missingScript.Content.ReadAsStringAsync()).ShouldNotContain("recall spa");
        noAccept.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        reserved.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        reservedApi.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task the_spa_answers_a_head_and_no_other_verb()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var head = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/about"));
        using var post = await server.Client.PostAsync("/about", new StringContent(""));
        using var delete = await server.Client.DeleteAsync("/");

        // Assert
        head.StatusCode.ShouldBe(HttpStatusCode.OK);
        head.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        head.Content.Headers.ContentLength.ShouldBe("<html>recall spa</html>".Length);
        post.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task a_path_of_the_server_never_gets_the_spa()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        foreach (var path in new[] { "/health/nope", "/alive/nope", "/mcp/nope", "/swagger", "/openapi", "/API/nope", "/.well-known/nope", "/backwave", "/backwave/jobs" })
        {
            // Act
            using var response = await GetAsync(server, path, "text/html");

            // Assert
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("recall spa", customMessage: path);
        }
    }

    [Test]
    public async Task an_unknown_api_route_gets_a_json_404_and_a_known_route_keeps_its_405()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var unknown = await GetAsync(server, "/api/v1/nope", "text/html");
        using var wrongVerb = await server.Client.DeleteAsync("/api/v1/tenants");

        // Assert
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32().ShouldBe(404);
        wrongVerb.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    private static Task<HttpResponseMessage> GetAsync(ServerProcess server, string path, string? accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{server.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)}{path}"));
        if (accept is not null)
            request.Headers.Accept.ParseAdd(accept);
        return server.Client.SendAsync(request);
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

    [Test]
    public async Task a_failed_startup_exits_with_a_nonzero_code_and_says_why()
    {
        // Arrange
        Dictionary<string, string> badProvider = new()
        {
            ["Auth__Providers__0__Id"] = "stub",
            ["Auth__Providers__0__Issuer"] = "http://provider.test",
            ["Auth__Providers__0__ClientId"] = "recall",
            ["Auth__Providers__0__ClientSecret"] = "secret",
        };
        await using var server = await ServerProcess.LaunchAsync(null, badProvider);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldNotBe(0);
        server.Output.ShouldContain("AllowHttp");
    }

    [Test]
    public async Task a_keys_directory_with_no_access_does_not_stop_the_boot()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
        var keys = Path.Combine(root, "data", "keys");
        Directory.CreateDirectory(keys);
        var key = Path.Combine(keys, "key-loose.xml");
        await File.WriteAllTextAsync(key, "<key />");
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(keys, UnixFileMode.None);

        // Act
        await using var server = await ServerProcess.StartAsync(root);

        // Assert
        File.GetUnixFileMode(keys).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(key).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task the_data_protection_keys_are_for_the_owner_only_and_survive_a_restart()
    {
        // Arrange
        await using var first = await ServerProcess.StartAsync();
        var keys = first.KeyFiles();
        var directoryMode = File.GetUnixFileMode(first.KeysDirectory);
        var keyMode = File.GetUnixFileMode(keys.ShouldHaveSingleItem());
        var key = await File.ReadAllTextAsync(keys[0]);
        await first.StopAsync();

        // Act
        await using var second = await ServerProcess.StartAsync(first.Root);

        // Assert
        directoryMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        keyMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        second.KeyFiles().ShouldBe(keys);
        (await File.ReadAllTextAsync(keys[0])).ShouldBe(key);
    }
}
