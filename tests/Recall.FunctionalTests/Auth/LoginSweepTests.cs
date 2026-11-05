namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Recall.Server.Auth;
using Serilog.Events;
using Shouldly;

/// <summary>
/// A full web login and a full native login with a refresh, on an app that logs at Debug. No raw credential may reach a
/// log line or a span. The app sends the logs of Microsoft.Extensions.Logging through Serilog, so the capture is a
/// Serilog sink, the same place each line of the app goes through.
/// </summary>
public class LoginSweepTests
{
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
        await browser.GetAsync("/bff/user", csrf: true);

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
        ]);

        // A span ends after its response goes out, so the last ones need a moment to reach the listener
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert
        var logLines = host.Logs.All.Select(TextOf).ToList();
        // The capture works: the login routes were seen at Debug, by both, or an empty capture would pass.
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
