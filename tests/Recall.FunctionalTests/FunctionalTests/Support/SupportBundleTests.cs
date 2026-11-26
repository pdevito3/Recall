namespace Recall.FunctionalTests.FunctionalTests.Support;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.FunctionalTests.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Support;
using Recall.Server.Domain.Support.Features;
using Recall.Server.Resources.Entrypoints;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;
using TestUtilities;

[NotInParallel(nameof(SupportBundleTests))]
public partial class SupportBundleTests
{
    private const string OwnerSubject = "bundle-owner";

    private static AuthFlowFactory Host => AuthHosts.Bundle;

    private static async Task<AuthBrowser> SignedInAsync(string subject)
    {
        var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(subject);
        return browser;
    }

    private static Task<AuthBrowser> OwnerAsync() => SignedInAsync(OwnerSubject);

    private static Task<string> UserIdOfAsync(string subject) => Host.WithDatabaseAsync(database => database.Set<ExternalLogin>()
        .IgnoreQueryFilters()
        .Where(login => login.Subject == subject)
        .Select(login => login.UserId)
        .SingleAsync());

    private static async Task<HttpResponseMessage> SendJsonAsync(AuthBrowser browser, HttpMethod method, string route, object body)
    {
        var request = new HttpRequestMessage(method, $"/{route}") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return await browser.SendAsync(request);
    }

    private static async Task<Dictionary<string, string>> EntriesOfBundleAsync(AuthBrowser browser)
    {
        var response = await browser.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/zip");
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        await using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            await using var content = await entry.OpenAsync();
            using var reader = new StreamReader(content, Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    private static async Task<JsonElement> McpResultAsync(AuthBrowser browser, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EntrypointRegistration.McpPath)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
        };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await browser.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = (await response.Content.ReadAsStringAsync()).Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result").Clone();
    }

    /// <summary>The schema of each tool gives its arguments, so a new tool gets the marker in each of its text arguments.</summary>
    private static async Task<int> CallEachMcpToolAsync(AuthBrowser browser, string marker, string tenantId)
    {
        var tools = (await McpResultAsync(browser, "tools/list", new { })).GetProperty("tools").EnumerateArray().ToList();
        foreach (var tool in tools)
            await McpResultAsync(browser, "tools/call", new { name = tool.GetProperty("name").GetString(), arguments = McpArguments(tool.GetProperty("inputSchema"), marker, tenantId) });

        return tools.Count;
    }

    private static Dictionary<string, object?> McpArguments(JsonElement schema, string marker, string tenantId) =>
        schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().ToDictionary(item => item.Name, item => McpArgument(item.Name, item.Value, marker, tenantId))
            : [];

    private static object? McpArgument(string name, JsonElement schema, string marker, string tenantId)
    {
        var type = schema.TryGetProperty("type", out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().First(item => item.GetString() != "null").GetString()
            : property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return type switch
        {
            "integer" or "number" => 1,
            "boolean" => true,
            "array" => new[] { McpArgument(name, schema.GetProperty("items"), marker, tenantId) },
            "object" => McpArguments(schema, marker, tenantId),
            // An ID is a protocol field that may reach a span. See the privacy rule in docs/telemetry.md.
            _ => name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Id", StringComparison.Ordinal) ? tenantId : marker,
        };
    }

    [Test]
    public async Task the_owner_downloads_one_zip_with_logs_telemetry_version_and_settings_with_no_secret()
    {
        // Arrange
        using var owner = await OwnerAsync();
        using var firstRequest = await owner.GetAsync($"/{ApiRoutes.Users.GetList}", csrf: true);

        // Act
        var entries = await EntriesOfBundleAsync(owner);

        // Assert
        entries.Keys.ShouldContain(name => name.StartsWith("logs/recall-", StringComparison.Ordinal));
        entries.Keys.ShouldContain(name => name.StartsWith("telemetry/traces-", StringComparison.Ordinal));
        entries.Keys.ShouldContain(name => name.StartsWith("telemetry/metrics-", StringComparison.Ordinal));
        JsonDocument.Parse(entries["version.json"]).RootElement.GetProperty("Version").GetString().ShouldNotBeNullOrEmpty();
        var settings = JsonDocument.Parse(entries["settings.json"]).RootElement;
        settings.GetProperty("Auth:Providers:0:ClientSecret").GetString().ShouldBe(RedactedSettings.Hidden);
        settings.GetProperty("Auth:Providers:0:ClientId").GetString().ShouldBe(StubOidcProvider.ClientId);
        foreach (var (name, content) in entries)
            content.ShouldNotContain(owner.Provider.ClientSecret, customMessage: name);
    }

    [Test]
    public async Task a_member_and_a_second_admin_get_a_403_that_names_the_owner()
    {
        // Arrange
        using var owner = await OwnerAsync();
        using var member = await SignedInAsync($"bundle-member-{Guid.NewGuid():N}");
        var adminSubject = $"bundle-admin-{Guid.NewGuid():N}";
        using var admin = await SignedInAsync(adminSubject);
        var promoted = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Users.AddRole(await UserIdOfAsync(adminSubject)), new { role = "Admin" });
        promoted.IsSuccessStatusCode.ShouldBeTrue();

        // Act
        var byMember = await member.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true);
        var byAdmin = await admin.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true);

        // Assert
        byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await byAdmin.Content.ReadAsStringAsync()).ShouldContain(GetSupportBundle.OnlyTheOwner);
    }

    [Test]
    public async Task a_demoted_owner_gets_a_403_and_the_next_oldest_admin_becomes_the_owner()
    {
        // Arrange
        var host = AuthHosts.OwnerDemotion;
        using var owner = new AuthBrowser(host);
        await owner.LoginWebAsync("demoted-owner");
        using var next = new AuthBrowser(host);
        await next.LoginWebAsync("next-admin");
        using var later = new AuthBrowser(host);
        await later.LoginWebAsync("later-admin");
        var ids = await host.WithDatabaseAsync(database => database.Set<ExternalLogin>().IgnoreQueryFilters().ToDictionaryAsync(login => login.Subject, login => login.UserId));
        (await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Users.AddRole(ids["next-admin"]), new { role = "Admin" })).IsSuccessStatusCode.ShouldBeTrue();
        (await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Users.AddRole(ids["later-admin"]), new { role = "Admin" })).IsSuccessStatusCode.ShouldBeTrue();
        (await owner.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var demoted = await SendJsonAsync(next, HttpMethod.Delete, ApiRoutes.Users.RemoveRole(ids["demoted-owner"], "Admin"), new { });

        // Assert
        demoted.IsSuccessStatusCode.ShouldBeTrue(await demoted.Content.ReadAsStringAsync());
        (await owner.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await next.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await later.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task text_from_a_caller_reaches_no_log_no_span_and_no_file_of_the_bundle()
    {
        // Arrange
        using var owner = await OwnerAsync();
        var ownerId = await UserIdOfAsync(OwnerSubject);
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        using var read = await owner.GetAsync($"/{ApiRoutes.Users.GetRecord(ownerId)}", csrf: true);
        var version = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetUInt32();

        // Act
        using var filter = await owner.GetAsync($"/{ApiRoutes.Users.GetList}?filters=firstName @=* \"{marker}\"", csrf: true);
        using var badFilter = await owner.GetAsync($"/{ApiRoutes.Users.GetList}?filters={marker}", csrf: true);
        using var badSort = await owner.GetAsync($"/{ApiRoutes.Users.GetList}?sortOrder={marker}", csrf: true);
        using var queryWithNoValue = await owner.GetAsync($"/{ApiRoutes.Users.GetList}?{marker}", csrf: true);
        using var auditFilter = await owner.GetAsync($"/{ApiRoutes.AuditEntries.GetPage(marker)}", csrf: true);
        using var requestWithAgent = new HttpRequestMessage(HttpMethod.Get, $"/{ApiRoutes.Users.GetList}");
        requestWithAgent.Headers.Add("X-CSRF", "1");
        requestWithAgent.Headers.UserAgent.ParseAdd(marker);
        using var withAgent = await owner.SendAsync(requestWithAgent);
        using var badBody = await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Users.Put(ownerId), new { firstName = new string('x', 5000) + marker, username = marker, email = marker, version });
        using var rename = await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Users.Put(ownerId), new { firstName = marker, lastName = marker, username = marker, email = $"{marker}@example.com", version });
        var tenantId = await Host.WithDatabaseAsync(database => database.Users.IgnoreQueryFilters().Where(user => user.Id == ownerId).Select(user => user.TenantId).SingleAsync());
        var toolsCalled = await CallEachMcpToolAsync(owner, marker, tenantId!);
        await WaitUntilEachSinkHoldsAsync((await AddSessionAsync(owner)).Id);
        var entries = await EntriesOfBundleAsync(owner);

        // Assert
        badSort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badSort.Content.ReadAsStringAsync()).ShouldContain(marker);
        rename.IsSuccessStatusCode.ShouldBeTrue(await rename.Content.ReadAsStringAsync());
        toolsCalled.ShouldBeGreaterThan(0);
        badFilter.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await badFilter.Content.ReadAsStringAsync());
        badBody.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await badBody.Content.ReadAsStringAsync());
        filter.StatusCode.ShouldBe(HttpStatusCode.OK, await filter.Content.ReadAsStringAsync());
        auditFilter.StatusCode.ShouldBe(HttpStatusCode.OK, await auditFilter.Content.ReadAsStringAsync());
        withAgent.StatusCode.ShouldBe(HttpStatusCode.OK, await withAgent.Content.ReadAsStringAsync());
        entries.Keys.Count(name => name.StartsWith("telemetry/traces-", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        ShouldHoldNoMarker(marker, entries);
    }
}
