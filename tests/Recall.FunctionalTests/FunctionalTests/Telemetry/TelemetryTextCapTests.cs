namespace Recall.FunctionalTests.FunctionalTests.Telemetry;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Recall.Server.Resources;
using Recall.Server.Resources.Entrypoints;
using Shouldly;

// One test sends long values, and the other reads each span for a marker, so the two do not run at the same time
[NotInParallel]
public class TelemetryTextCapTests : TestBase
{
    private static string Long => new('<', 100_000);

    private void SignInAsTelemetryClient()
        => FactoryClient.SetFakeBearerToken(new Dictionary<string, object>
        {
            ["client_id"] = TestingWebApplicationFactory.TelemetryMachineClientId,
        });

    private static string[] TextsOf(Activity span) =>
    [
        span.DisplayName,
        span.StatusDescription ?? string.Empty,
        .. span.TagObjects.Select(tag => tag.Value).OfType<string>(),
        .. span.Events.SelectMany(spanEvent => spanEvent.Tags).Select(tag => tag.Value).OfType<string>(),
    ];

    private async Task<HttpStatusCode> PostToMcpAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, EntrypointRegistration.McpPath) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var result = await FactoryClient.SendAsync(request);
        return result.StatusCode;
    }

    private static string CallGetTenant(string id) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "get_tenant", arguments = new { id } } });

    private static string Initialize(string clientName) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = clientName, version = "1" } },
        });

    private static string UnknownMethod(string method) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } });

    private static async Task<string[]> TextsOfNewSpansAsync(int before)
    {
        // The request span ends after the response goes, so it can arrive a moment later
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        return [.. AllExportedSpans().Skip(before).SelectMany(TextsOf)];
    }

    [Test]
    public async Task each_string_of_a_caller_in_an_mcp_call_is_inside_the_cap_in_each_span()
    {
        // Arrange
        SignInAsTelemetryClient();
        var before = AllExportedSpans().Count;

        // Act
        var statuses = new[]
        {
            await PostToMcpAsync(CallGetTenant(Long)),
            await PostToMcpAsync(UnknownMethod(Long)),
            await PostToMcpAsync(Initialize(Long)),
        };
        var texts = await TextsOfNewSpansAsync(before);

        // Assert
        statuses.ShouldAllBe(status => status == HttpStatusCode.OK);
        texts.ShouldNotBeEmpty();
        texts.ShouldAllBe(text => text.Length <= TelemetryText.MaxLength);
    }

    [Test]
    public async Task a_real_failure_keeps_each_field_whole_with_no_marker()
    {
        // Arrange
        SignInAsTelemetryClient();
        var before = AllExportedSpans().Count;

        // Act
        await PostToMcpAsync(CallGetTenant(Guid.NewGuid().ToString()));
        await PostToMcpAsync(CallGetTenant("not-an-id"));
        await PostToMcpAsync(UnknownMethod("nothing/here"));
        var texts = await TextsOfNewSpansAsync(before);

        // Assert
        texts.ShouldNotBeEmpty();
        texts.ShouldAllBe(text => !text.Contains(TelemetryText.CutMarker, StringComparison.Ordinal));
    }
}
