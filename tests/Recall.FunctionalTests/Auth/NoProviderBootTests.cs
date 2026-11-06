namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
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
        private bool _killed;

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

        public string Output => string.Join('\n', _output);

        public static async Task<ServerProcess> StartAsync(string? existingRoot = null)
        {
            var server = await LaunchAsync(existingRoot, new Dictionary<string, string>());
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
            await File.WriteAllTextAsync(Path.Combine(web, "app.js"), "// recall spa");

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

        public async Task WaitUntilUpAsync()
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

        /// <summary>A SIGKILL, so the last connection runs no checkpoint on its close, and the files stay as a crash leaves them.</summary>
        public async Task KillAsync()
        {
            if (_killed)
                return;

            _killed = true;
            Client.Dispose();
            await StopAsync();
            _process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        [GeneratedRegex(@"http://127\.0\.0\.1:[1-9][0-9]*")]
        private static partial Regex ListeningAddress();

        private static string ServerDll() =>
            Path.Combine(TestBuild.RepositoryRoot(), "Recall.Server", "bin", TestBuild.Configuration, "net10.0", "Recall.Server.dll");
    }

    private static class TestBuild
    {
#if DEBUG
        public const string Configuration = "Debug";
#else
        public const string Configuration = "Release";
#endif

        public static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
                directory = directory.Parent;
            directory.ShouldNotBeNull();
            return directory.FullName;
        }
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_restart_after_a_kill_clears_committed_deletes_from_the_wal()
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
    public async Task the_html_of_the_app_tells_the_browser_to_store_nothing()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var index = await server.Client.GetAsync("/");
        using var route = await server.Client.GetAsync("/users");

        // Assert
        // With this header, Chromium did not restore a page after its cookies were removed. docs/authentication.md has the runs.
        index.Headers.CacheControl!.NoStore.ShouldBeTrue();
        route.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Test]
    public async Task a_static_file_of_the_app_has_no_private_data_so_the_browser_can_keep_it()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var file = await server.Client.GetAsync("/app.js");

        // Assert
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Headers.CacheControl?.NoStore.ShouldNotBe(true);
    }

    [Test]
    public async Task each_answer_of_the_spa_has_the_content_security_policy_and_a_server_path_does_not()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        // Act
        using var root = await GetAsync(server, "/", "text/html");
        using var deepLink = await GetAsync(server, "/users/1", "text/html");
        using var file = await GetAsync(server, "/index.html", "text/html");
        using var head = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/about"));
        using var health = await GetAsync(server, "/health", null);

        // Assert
        foreach (var answer in new[] { root, deepLink, file, head })
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.OK);
            var policy = answer.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
            policy.ShouldContain("frame-ancestors 'none'");
            policy.ShouldContain("script-src 'self';");
        }

        health.Headers.Contains("Content-Security-Policy").ShouldBeFalse();
    }

    [Test]
    public async Task a_path_of_the_server_never_gets_the_spa()
    {
        // Arrange
        await using var server = await ServerProcess.StartAsync();

        foreach (var path in new[] { "/health/nope", "/alive/nope", "/mcp/nope", "/swagger", "/openapi", "/API/nope", "/.well-known/nope" })
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
    [Arguments("maybe")]
    [Arguments("yes")]
    [Arguments("1")]
    [Arguments("")]
    public async Task a_provider_setting_that_cannot_convert_stops_the_server_and_names_the_setting(string allowHttp)
    {
        // Arrange
        const string secret = "a-secret-that-no-log-shows";
        Dictionary<string, string> provider = new()
        {
            ["Auth__Providers__0__Id"] = "stub",
            ["Auth__Providers__0__Issuer"] = "https://provider.test",
            ["Auth__Providers__0__ClientId"] = "recall",
            ["Auth__Providers__0__ClientSecret"] = secret,
            ["Auth__Providers__0__AllowHttp"] = allowHttp,
        };
        await using var server = await ServerProcess.LaunchAsync(null, provider);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldBe(1);
        server.Output.ShouldContain("Auth:Providers:0:AllowHttp");
        server.Output.ShouldNotContain(secret);
    }

    [Test]
    public async Task a_bad_setting_of_a_later_provider_does_not_leave_the_first_provider_alone_on_the_login_page()
    {
        // Arrange
        Dictionary<string, string> providers = new()
        {
            ["Auth__Providers__0__Id"] = "first",
            ["Auth__Providers__0__Issuer"] = "https://first.test",
            ["Auth__Providers__0__ClientId"] = "recall",
            ["Auth__Providers__0__ClientSecret"] = "secret",
            ["Auth__Providers__1__Id"] = "second",
            ["Auth__Providers__1__Issuer"] = "https://second.test",
            ["Auth__Providers__1__ClientId"] = "recall",
            ["Auth__Providers__1__ClientSecret"] = "secret",
            ["Auth__Providers__1__AllowHttp"] = "maybe",
        };
        await using var server = await ServerProcess.LaunchAsync(null, providers);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldBe(1);
        server.Output.ShouldContain("Auth:Providers:1:AllowHttp");
    }

    [Test]
    public async Task a_provider_with_a_text_value_in_place_of_its_settings_stops_the_server_and_names_the_provider()
    {
        // Arrange
        Dictionary<string, string> provider = new() { ["Auth__Providers__0"] = "keycloak" };
        await using var server = await ServerProcess.LaunchAsync(null, provider);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldBe(1);
        server.Output.ShouldContain("Auth:Providers:0:ClientId is missing");
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
