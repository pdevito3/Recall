namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth;
using Recall.Server.Resources.Entrypoints.Auth;
using Serilog.Events;
using Shouldly;

[NotInParallel("owner-claim-file")]
public class LoginSweepTests
{
    private static readonly string[] SweepGrant = ["manage_user_access", "read_agents", "read_users"];

    [Test]
    public async Task no_raw_token_code_state_nonce_verifier_key_or_secret_reaches_a_log_or_a_span()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var spans = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Enqueue(TextOf(activity)),
        };
        ActivitySource.AddActivityListener(listener);
        var secrets = new HashSet<string> { host.Provider.ClientSecret };

        // Act
        using var browser = new AuthBrowser(host);
        var authorizeWeb = await browser.StartWebAsync("/after-login");
        var bindingWeb = browser.Cookies.Single(cookie => cookie.Key.StartsWith("recall_login_", StringComparison.Ordinal)).Value.Value;
        (await browser.FinishAtProviderAsync(authorizeWeb, new StubLogin($"sweep-{Guid.NewGuid():N}"))).Headers.Location.ShouldNotBeNull();
        var cookie = browser.SessionCookie.ShouldNotBeNull();
        await browser.GetAsync("/auth/me", csrf: true);

        using var phone = new AuthBrowser(host);
        var app = NativeApp.New();
        var authorizeNative = await phone.StartNativeAsync(app);
        var bindingNative = phone.Cookies.Single(cookie => cookie.Key.StartsWith("recall_login_", StringComparison.Ordinal)).Value.Value;
        var page = await phone.FinishAtProviderAsync(authorizeNative, new StubLogin($"sweep-{Guid.NewGuid():N}"));
        var code = AuthBrowser.CodeInPage(await page.Content.ReadAsStringAsync());
        await phone.PostFormAsync("/auth/oidc/confirm", [("code", code)]);
        var first = await AuthBrowser.TokensOf(await phone.ExchangeAsync(code, app.Verifier, app.Redirect));
        await phone.GetAsync("/auth/me", authorization: $"Bearer {first.AccessToken}");
        var retryKey = Secrets.New();
        var second = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, retryKey));
        await phone.PostFormAsync("/auth/logout", [("refresh_token", second.RefreshToken)]);

        secrets.UnionWith(host.Provider.HandedOut);
        secrets.UnionWith(
        [
            authorizeWeb.State, authorizeWeb.Nonce, authorizeWeb.CodeChallenge, bindingWeb, cookie,
            authorizeNative.State, authorizeNative.Nonce, authorizeNative.CodeChallenge, bindingNative,
            app.Verifier, app.Challenge, app.ClientState, code,
            first.AccessToken, first.RefreshToken, second.AccessToken, second.RefreshToken, retryKey,
            FamilyOf(first.RefreshToken), FamilyOf(second.RefreshToken),
        ]);

        // A span ends after its response goes out, so the last ones need a moment to reach the listener
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert
        var logLines = host.Logs.All.Select(TextOf).ToList();
        logLines.ShouldContain(line => line.Contains("/auth/token", StringComparison.Ordinal));
        logLines.ShouldContain(line => line.Contains("Debug", StringComparison.Ordinal));
        logLines.ShouldContain(line => line.Contains("auth_sessions", StringComparison.Ordinal));
        spans.ShouldContain(span => span.Contains("/auth/token", StringComparison.Ordinal));
        spans.ShouldContain(span => span.Contains("/auth/oidc/callback", StringComparison.Ordinal));

        foreach (var secret in secrets)
        {
            logLines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal));
            spans.ShouldNotContain(span => span.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Test]
    [Property("Rule", "PAT-17")]
    [Property("Rule", "PAT-14")]
    public async Task no_value_or_hash_of_a_personal_access_token_reaches_a_log_a_span_or_an_audit_value()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var spans = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Enqueue(TextOf(activity)),
        };
        ActivitySource.AddActivityListener(listener);
        var (admin, member) = await PatPeople.AddTenantAsync(host);
        using var browser = new AuthBrowser(host);
        await browser.LoginWebAsync(admin.Subject);

        // Act
        var created = await AsThePageAsync(browser, HttpMethod.Post, "/api/v1/personal-access-tokens", new
        {
            name = "Sweep token",
            permissions = SweepGrant,
            readJournal = true,
            writeJournal = true,
            neverExpires = true,
        });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var value = body.GetProperty("value").GetString()!;
        var tokenId = body.GetProperty("token").GetProperty("id").GetString()!;
        using var client = PatPeople.ClientWith(host, value);
        var write = await client.PostAsJsonAsync($"/api/v1/users/{member.UserId}/permissions", new { permission = "read_agents" });
        await using (var mcp = await PatPeople.McpAsync(client))
            await mcp.ListToolsAsync();
        var revoke = await AsThePageAsync(browser, HttpMethod.Post, $"/api/v1/personal-access-tokens/{tokenId}/revoke");
        var memberAudit = await (await AsThePageAsync(browser, HttpMethod.Get, $"/{ApiRoutes.AuditEntries.GetPage(member.UserId)}")).Content.ReadAsStringAsync();
        var adminAudit = await (await AsThePageAsync(browser, HttpMethod.Get, $"/{ApiRoutes.AuditEntries.GetPage(admin.UserId)}")).Content.ReadAsStringAsync();
        string[] markers = [value, value[NewPersonalAccessToken.Prefix.Length..], TokenHash.Of(value).Hex];

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert
        created.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        write.IsSuccessStatusCode.ShouldBeTrue();
        revoke.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        var actorOfWrite = JsonDocument.Parse(memberAudit).RootElement.GetProperty("items").EnumerateArray()
            .First(entry => entry.GetProperty("entityType").GetString() == "UserPermission").GetProperty("actorClient").GetString();
        actorOfWrite.ShouldBe(tokenId);
        adminAudit.ShouldContain("access_token_created");
        adminAudit.ShouldContain("access_token_revoked");
        adminAudit.ShouldContain("Sweep token");
        var logLines = host.Logs.All.Select(TextOf).ToList();
        logLines.ShouldContain(line => line.Contains(tokenId, StringComparison.Ordinal));
        spans.ShouldContain(span => span.Contains("personal-access-tokens", StringComparison.Ordinal));
        foreach (var marker in markers)
        {
            logLines.ShouldNotContain(line => line.Contains(marker, StringComparison.Ordinal));
            spans.ShouldNotContain(span => span.Contains(marker, StringComparison.Ordinal));
            memberAudit.ShouldNotContain(marker);
            adminAudit.ShouldNotContain(marker);
        }
    }

    private static async Task<HttpResponseMessage> AsThePageAsync(AuthBrowser browser, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
    }

    private static string FamilyOf(string refreshToken)
    {
        RefreshToken.TryParse(refreshToken, out var parsed).ShouldBeTrue();
        return parsed!.Family;
    }

    private static string TextOf(LogEvent logEvent)
        => string.Join(
            '\n',
            new[] { logEvent.Level.ToString(), logEvent.RenderMessage(CultureInfo.InvariantCulture), logEvent.Exception?.ToString() ?? "" }
                .Concat(logEvent.Properties.Select(property => $"{property.Key}={property.Value}")));

    private static string TextOf(Activity activity)
        => string.Join(
            '\n',
            new[] { activity.OperationName, activity.DisplayName, activity.StatusDescription ?? "" }
                .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
                .Concat(activity.Baggage.Select(item => $"{item.Key}={item.Value}"))
                .Concat(activity.Events.SelectMany(activityEvent =>
                    new[] { activityEvent.Name }.Concat(activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")))));
}
