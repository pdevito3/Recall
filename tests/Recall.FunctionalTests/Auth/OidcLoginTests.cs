namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

public class OidcLoginTests
{
    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";

    private static AuthBrowser NewBrowser() => new(AuthHosts.Open);

    private static async Task<string> SubjectOfSessionAsync(AuthBrowser browser)
    {
        var me = await browser.GetAsync("/auth/me", csrf: true);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sub").GetString()!;
    }

    [Test]
    public async Task on_plain_http_the_cookies_have_the_plain_names()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        await browser.LoginWebAsync(NewSubject());

        // Assert
        var binding = browser.SetCookieLines.First(line => line.StartsWith("recall_login_", StringComparison.Ordinal)).ToLowerInvariant();
        binding.ShouldContain("path=/auth/oidc");
        binding.ShouldContain("samesite=lax");
        binding.ShouldContain("httponly");
        binding.ShouldNotContain("secure");
        var session = browser.SetCookieLines.Single(line => line.StartsWith("recall=", StringComparison.Ordinal)).ToLowerInvariant();
        session.ShouldContain("path=/;");
        session.ShouldContain("samesite=strict");
        session.ShouldContain("httponly");
        session.ShouldNotContain("secure");
        session.ShouldNotContain("domain");
    }

    [Test]
    public async Task with_x_forwarded_proto_https_the_cookies_have_the_host_prefix()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Open, tls: true);

        // Act
        var authorize = await browser.StartWebAsync();
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        authorize.RedirectUri.ShouldBe("https://localhost/auth/oidc/callback");
        var binding = browser.SetCookieLines.First(line => line.StartsWith("__Host-recall_login_", StringComparison.Ordinal)).ToLowerInvariant();
        binding.ShouldContain("path=/;");
        binding.ShouldContain("secure");
        binding.ShouldNotContain("domain");
        var session = browser.SetCookieLines.Single(line => line.StartsWith("__Host-recall=", StringComparison.Ordinal)).ToLowerInvariant();
        session.ShouldContain("path=/;");
        session.ShouldContain("secure");
        session.ShouldContain("samesite=strict");
        session.ShouldContain("httponly");
        session.ShouldNotContain("domain");
        (await browser.GetAsync("/auth/me", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task with_x_forwarded_proto_https_the_plain_cookie_name_is_not_read()
    {
        // Arrange
        using var signedIn = new AuthBrowser(AuthHosts.Open, tls: true);
        await signedIn.LoginWebAsync(NewSubject());
        using var planted = new AuthBrowser(AuthHosts.Open, tls: true);
        planted.SetCookie("recall", signedIn.SessionCookie!);

        // Act
        var response = await planted.GetAsync("/auth/me", csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task the_callback_clears_the_binding_cookie()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        await browser.LoginWebAsync(NewSubject());

        // Assert
        browser.Cookies.Keys.ShouldNotContain(name => name.StartsWith("recall_login_", StringComparison.Ordinal));
    }

    [Test]
    public async Task two_tabs_keep_their_own_login_in_flight()
    {
        // Arrange
        using var browser = NewBrowser();
        var firstTab = await browser.StartWebAsync("/first");
        var secondTab = await browser.StartWebAsync("/second");

        // Act
        var first = await browser.FinishAtProviderAsync(firstTab, new StubLogin(NewSubject()));
        var second = await browser.FinishAtProviderAsync(secondTab, new StubLogin(NewSubject()));

        // Assert
        first.Headers.Location!.OriginalString.ShouldBe("/first");
        second.Headers.Location!.OriginalString.ShouldBe("/second");
    }

    [Test]
    public async Task the_same_subject_of_one_issuer_gives_the_same_user_and_another_subject_another()
    {
        // Arrange
        var subject = NewSubject();
        using var first = NewBrowser();
        using var again = NewBrowser();
        using var other = NewBrowser();

        // Act
        await first.LoginWebAsync(subject);
        await again.LoginWebAsync(subject);
        await other.LoginWebAsync(NewSubject());

        // Assert
        (await SubjectOfSessionAsync(again)).ShouldBe(await SubjectOfSessionAsync(first));
        (await SubjectOfSessionAsync(other)).ShouldNotBe(await SubjectOfSessionAsync(first));
    }

    [Test]
    public async Task with_closed_signup_the_first_login_is_the_owner_and_a_second_person_gets_the_refusal_page()
    {
        // Arrange
        using var owner = new AuthBrowser(AuthHosts.Closed);
        using var stranger = new AuthBrowser(AuthHosts.Closed);
        await owner.LoginWebAsync("owner");

        // Act
        var refused = await stranger.FinishAtProviderAsync(await stranger.StartWebAsync(), new StubLogin(NewSubject()));
        var ownerAgain = await owner.GetAsync("/auth/me", csrf: true);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.SessionCookie.ShouldBeNull();
        ownerAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task with_closed_signup_a_native_login_of_a_second_person_goes_back_with_signup_closed()
    {
        // Arrange
        using var owner = new AuthBrowser(AuthHosts.Closed);
        using var stranger = new AuthBrowser(AuthHosts.Closed);
        await owner.LoginWebAsync("owner");
        var app = NativeApp.New();

        // Act
        var response = await stranger.FinishAtProviderAsync(await stranger.StartNativeAsync(app), new StubLogin(NewSubject()));

        // Assert
        AssertNativeError(response, app, "signup_closed");
    }

    private static void AssertNativeError(HttpResponseMessage response, NativeApp app, string error)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.ShouldStartWith(app.Redirect + "?");
        var query = QueryHelpers.ParseQuery(location[app.Redirect.Length..]);
        query["error"].ToString().ShouldBe(error);
        query["state"].ToString().ShouldBe(app.ClientState);
        query.Keys.ShouldNotContain("code");
    }

    [Test]
    [Arguments("access_denied", "access_denied")]
    [Arguments("temporarily_unavailable", "idp_unreachable")]
    [Arguments("server_error", "idp_unreachable")]
    [Arguments("other_error", "invalid_response")]
    public async Task a_provider_error_on_a_native_login_goes_back_to_the_app(string providerError, string expected)
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var authorize = await browser.StartNativeAsync(app);

        // Act
        var response = await browser.CallbackAsync(authorize.State, code: null, error: providerError);

        // Assert
        AssertNativeError(response, app, expected);
    }

    [Test]
    public async Task a_bad_id_token_on_a_native_login_goes_back_to_the_loopback_redirect()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New("http://127.0.0.1:7000/cb");
        var authorize = await browser.StartNativeAsync(app);

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { Nonce = "other" });

        // Assert
        AssertNativeError(response, app, "invalid_response");
    }

    [Test]
    [Arguments("access_denied", HttpStatusCode.Forbidden)]
    [Arguments("temporarily_unavailable", HttpStatusCode.ServiceUnavailable)]
    [Arguments("other_error", HttpStatusCode.BadRequest)]
    public async Task a_provider_error_on_a_web_login_shows_a_page(string providerError, HttpStatusCode expected)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.CallbackAsync(authorize.State, code: null, error: providerError);

        // Assert
        response.StatusCode.ShouldBe(expected);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
    }

    [Test]
    public async Task a_native_callback_shows_a_confirm_page_and_the_code_reaches_the_app_only_after_the_step()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var authorize = await browser.StartNativeAsync(app);

        // Act
        var page = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));
        var html = await page.Content.ReadAsStringAsync();
        var code = AuthBrowser.CodeInPage(html);
        var confirmed = await browser.PostFormAsync("/auth/oidc/confirm", [("code", code)]);

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Headers.Location.ShouldBeNull();
        html.ShouldNotContain(NativeApp.AppCallback);
        html.ShouldContain("<h1>Sign in to the Recall app?</h1>");
        html[html.IndexOf("<h1>", StringComparison.Ordinal)..].Split("Sign in to the Recall app").Length.ShouldBe(2);
        html.ShouldContain("Ada Lovelace");
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        confirmed.Headers.Location!.OriginalString.ShouldBe($"{app.Redirect}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(app.ClientState)}");
        (await AuthBrowser.TokensOf(await browser.ExchangeAsync(code, app.Verifier, app.Redirect))).ExpiresIn.ShouldBe(900);
    }

    [Test]
    [Arguments("")]
    [Arguments("code=unknown")]
    public async Task a_confirm_with_no_live_code_is_refused(string body)
    {
        // Arrange
        using var browser = NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/oidc/confirm")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };

        // Act
        var response = await browser.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Test]
    public async Task the_status_says_how_to_sign_in_with_no_session()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/status");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("state").GetString().ShouldBe("ready");
        body.GetProperty("setupRequired").GetBoolean().ShouldBeFalse();
        body.GetProperty("callbackUrl").GetString().ShouldBe("http://localhost/auth/oidc/callback");
        var mode = body.GetProperty("modes").EnumerateArray().ShouldHaveSingleItem();
        mode.GetProperty("id").GetString().ShouldBe(AuthFlowFactory.ModeId);
        mode.GetProperty("kind").GetString().ShouldBe("oidc");
        mode.GetProperty("state").GetString().ShouldBe("ready");
        mode.GetProperty("startUrl").GetString().ShouldBe("/auth/oidc/stub/start?client=app");
    }

    [Test]
    public async Task after_a_sign_out_the_login_route_shows_the_chooser_and_starts_nothing()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/login?signedOut=1");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("/auth/oidc/stub/start?client=web");
    }
}
