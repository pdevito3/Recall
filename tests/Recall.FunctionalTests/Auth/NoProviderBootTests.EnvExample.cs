namespace Recall.FunctionalTests.Auth;

using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public partial class NoProviderBootTests
{
    private const string ProviderLinePrefix = "Auth__Providers__0__";

    private static Dictionary<string, string> Settings(IEnumerable<string> lines) =>
        lines.Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);

    private static string[] ExampleLines() =>
        File.ReadAllLines(Path.Combine(TestBuild.RepositoryRoot(), ".env.example"));

    [Test]
    public async Task the_example_settings_with_no_edit_give_the_password_login()
    {
        // Arrange
        var settings = Settings(ExampleLines().Where(line => line.Length > 0 && !line.StartsWith('#')));
        await using var server = await ServerProcess.LaunchAsync(null, settings);
        await server.WaitUntilUpAsync();

        // Act
        var status = await server.Client.GetFromJsonAsync<JsonElement>(AuthRoutes.Status);

        // Assert
        status.GetProperty("state").GetString().ShouldBe("ready");
        status.GetProperty("modes").EnumerateArray().ShouldHaveSingleItem().GetProperty("kind").GetString().ShouldBe("password");
    }

    [Test]
    [Arguments("none", "Auth:Providers:0:Issuer", "Auth:Providers:0:ClientId", "Auth:Providers:0:ClientSecret")]
    [Arguments("Issuer=https://provider.test", "Auth:Providers:0:ClientId", "Auth:Providers:0:ClientSecret", "ClientId is missing")]
    [Arguments("ClientId=recall", "Auth:Providers:0:Issuer", "Auth:Providers:0:ClientSecret", "ClientSecret is missing")]
    [Arguments("ClientSecret=secret", "Auth:Providers:0:Issuer", "Auth:Providers:0:ClientId", "ClientId is missing")]
    public async Task the_example_provider_lines_turned_on_with_only_some_values_stop_the_server(string filled, string named, string alsoNamed, string reason)
    {
        // Arrange
        var turnedOn = ExampleLines()
            .Where(line => line.StartsWith($"#{ProviderLinePrefix}", StringComparison.Ordinal) && !line.Contains("AllowHttp", StringComparison.Ordinal))
            .Select(line => line[1..]);
        var settings = Settings(turnedOn);
        settings.Keys.Select(key => key[ProviderLinePrefix.Length..]).ShouldBe(["Id", "DisplayName", "Issuer", "ClientId", "ClientSecret"], ignoreOrder: true);
        if (filled.Split('=', 2) is [var name, var value])
            settings[$"{ProviderLinePrefix}{name}"] = value;
        await using var server = await ServerProcess.LaunchAsync(null, settings);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await server.WaitForExitAsync(limit.Token);

        // Assert
        server.ExitCode.ShouldBe(1);
        server.Output.ShouldContain(named);
        server.Output.ShouldContain(alsoNamed);
        server.Output.ShouldContain(reason);
    }
}
