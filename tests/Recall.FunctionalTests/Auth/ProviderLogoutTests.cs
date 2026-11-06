namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

public class ProviderLogoutTests
{
    private const string SignedOutPage = "/auth/login?signedOut=1";
    private const string ReturnUrl = "http://localhost/auth/signed-out";

    private static readonly string ProviderLogoutUrlBeforeItsState =
        $"{StubOidcProvider.Issuer}/logout?client_id={StubOidcProvider.ClientId}&post_logout_redirect_uri={Uri.EscapeDataString(ReturnUrl)}&state=";

    private static async Task<AuthBrowser> SignedInBrowserAsync(AuthFlowFactory host)
    {
        var browser = new AuthBrowser(host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        return browser;
    }

    private static async Task<string?> ProviderLogoutUrlOf(HttpResponseMessage logout)
        => (await logout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider_logout_url").GetString();

    private static Task<HttpResponseMessage> LogoutAsync(AuthBrowser browser) => browser.PostFormAsync("/auth/logout", [], csrf: true);

    private static async Task<HttpResponseMessage> ReturnFromProviderAsync(AuthBrowser browser, HttpResponseMessage logout)
    {
        var url = (await ProviderLogoutUrlOf(logout))!;
        return await browser.GetAsync($"/auth/signed-out?state={url[ProviderLogoutUrlBeforeItsState.Length..]}");
    }

    [Test]
    public async Task a_browser_logout_ends_the_session_and_answers_the_logout_url_of_its_provider()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);

        // Act
        var logout = await LogoutAsync(browser);
        var me = await browser.GetAsync("/auth/me", csrf: true);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ProviderLogoutUrlOf(logout)).ShouldStartWith(ProviderLogoutUrlBeforeItsState);
        browser.SessionCookie.ShouldBeNull();
        me.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task a_logout_of_an_ended_session_still_answers_the_logout_url_of_its_provider()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        var staleTab = browser.SessionCookie!;
        await LogoutAsync(browser);
        browser.SetCookie(browser.SessionCookieName, staleTab);

        // Act
        var logout = await LogoutAsync(browser);

        // Assert
        (await ProviderLogoutUrlOf(logout)).ShouldStartWith(ProviderLogoutUrlBeforeItsState);
    }

    [Test]
    public async Task a_native_logout_answers_no_content_and_names_no_provider()
    {
        // Arrange
        using var phone = new AuthBrowser(AuthHosts.Open);
        var tokens = await phone.LoginNativeAsync($"subject-{Guid.NewGuid():N}");

        // Act
        var logout = await phone.PostFormAsync("/auth/logout", [], authorization: $"Bearer {tokens.AccessToken}");

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await logout.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task the_mark_of_a_sign_out_survives_the_cross_site_return_and_the_confirm_page_of_a_provider()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);

        // Act
        await LogoutAsync(browser);

        // Assert
        var mark = browser.SetCookieLines.Last();
        mark.ShouldStartWith("recall_signed_out=");
        mark.ShouldContain("samesite=lax", Case.Insensitive);
        mark.ShouldContain("max-age=2592000", Case.Insensitive);
        mark.ShouldContain("httponly", Case.Insensitive);
    }

    [Test]
    public async Task while_the_provider_has_not_sent_the_person_back_no_visit_starts_a_login()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        var logout = await LogoutAsync(browser);
        var providerLogoutUrl = await ProviderLogoutUrlOf(logout);

        // Act
        var plainVisit = await browser.GetAsync("/auth/login?returnUrl=%2Fentries");
        var entry = await browser.GetAsync(SignedOutPage);

        // Assert
        foreach (var page in new[] { plainVisit, entry })
        {
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            html.ShouldContain("<h1>You are signed out</h1>");
            html.ShouldContain("did not confirm the sign-out");
            html.ShouldContain($"<a href=\"{WebUtility.HtmlEncode(providerLogoutUrl)}\">Sign out of Stub</a>");
        }
    }

    [Test]
    public async Task the_return_from_the_provider_shows_the_signed_out_page_with_no_note_and_starts_nothing()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        var logout = await LogoutAsync(browser);

        // Act
        var back = await ReturnFromProviderAsync(browser, logout);
        var finishedMark = browser.SetCookieLines.Last();
        var page = await browser.GetAsync(back.Headers.Location!.OriginalString);

        // Assert
        finishedMark.ShouldContain("max-age=60", Case.Insensitive);
        back.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        back.Headers.Location!.OriginalString.ShouldBe(SignedOutPage);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>You are signed out</h1>");
        html.ShouldNotContain("Your session at");
        html.ShouldNotContain("did not confirm the sign-out");
        html.ShouldNotContain("Sign out of");
        html.ShouldContain("/auth/oidc/stub/start?client=web");
    }

    [Test]
    public async Task after_the_return_a_plain_visit_goes_to_the_provider_which_has_no_session_left()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        await ReturnFromProviderAsync(browser, await LogoutAsync(browser));

        // Act
        var plainVisit = await browser.GetAsync("/auth/login?returnUrl=%2Fentries");

        // Assert
        plainVisit.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        plainVisit.Headers.Location!.OriginalString.ShouldStartWith("/auth/oidc/stub/start?client=web");
    }

    [Test]
    [Arguments("/auth/signed-out")]
    [Arguments("/auth/signed-out?state=not-the-state")]
    public async Task a_link_to_the_return_address_cannot_finish_the_sign_out_at_the_provider(string address)
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        await LogoutAsync(browser);

        // Act
        var back = await browser.GetAsync(address);
        var plainVisit = await browser.GetAsync("/auth/login?returnUrl=%2Fentries");

        // Assert
        back.Headers.Location!.OriginalString.ShouldBe(SignedOutPage);
        plainVisit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await plainVisit.Content.ReadAsStringAsync()).ShouldContain("did not confirm the sign-out");
    }

    [Test]
    public async Task the_return_address_with_no_sign_out_says_nothing_about_a_sign_out()
    {
        // Arrange
        using var stranger = new AuthBrowser(AuthHosts.Open);

        // Act
        var back = await stranger.GetAsync("/auth/signed-out");
        var page = await stranger.GetAsync(back.Headers.Location!.OriginalString);

        // Assert
        stranger.SetCookieLines.ShouldNotContain(line => line.StartsWith("recall_signed_out=", StringComparison.Ordinal));
        (await page.Content.ReadAsStringAsync()).ShouldNotContain("You are signed out");
    }

    [Test]
    [Arguments("/auth/signed-out?returnUrl=https%3A%2F%2Fevil.example%2F")]
    [Arguments("/auth/signed-out?post_logout_redirect_uri=https%3A%2F%2Fevil.example%2F")]
    public async Task the_return_address_goes_only_to_the_signed_out_page(string address)
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Open);

        // Act
        var back = await browser.GetAsync(address);

        // Assert
        back.Headers.Location!.OriginalString.ShouldBe(SignedOutPage);
    }

    [Test]
    public async Task the_logout_url_comes_from_this_origin_and_never_from_a_return_value()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);

        // Act
        var logout = await browser.PostFormAsync("/auth/logout?returnUrl=https%3A%2F%2Fevil.example%2F", [("post_logout_redirect_uri", "https://evil.example/")], csrf: true);
        var page = await browser.GetAsync("/auth/login?signedOut=1&returnUrl=https%3A%2F%2Fevil.example%2F");

        // Assert
        (await ProviderLogoutUrlOf(logout)).ShouldStartWith(ProviderLogoutUrlBeforeItsState);
        (await page.Content.ReadAsStringAsync()).ShouldNotContain("evil.example");
    }

    [Test]
    [Arguments("open:nope")]
    [Arguments("open:a-state:nope")]
    [Arguments("open:https://evil.example/")]
    [Arguments("anything")]
    public async Task a_planted_mark_cannot_name_a_login_mode_that_this_server_does_not_have(string value)
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Open);
        browser.SetCookie("recall_signed_out", value);

        // Act
        var page = await browser.GetAsync(SignedOutPage);

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldNotContain("evil.example");
        html.ShouldNotContain("Your session at");
        html.ShouldNotContain("Sign out of");
    }

    [Test]
    public async Task a_new_login_deletes_the_mark_so_a_link_cannot_show_the_signed_out_page_next_to_a_live_session()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.Open);
        await LogoutAsync(browser);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");

        // Act: a link from another site sends the Lax cookies and not the Strict session cookie.
        browser.DropCookie(browser.SessionCookieName);
        var page = await browser.GetAsync(SignedOutPage);

        // Assert
        (await page.Content.ReadAsStringAsync()).ShouldNotContain("You are signed out");
    }

    [Test]
    public async Task a_provider_with_no_end_session_endpoint_keeps_the_behavior_and_the_page_says_that_its_session_stays()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync(AuthHosts.NoProviderLogout);

        // Act
        var logout = await LogoutAsync(browser);
        var page = await browser.GetAsync(SignedOutPage);
        var plainVisit = await browser.GetAsync("/auth/login?returnUrl=%2Fentries");

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        browser.SessionCookie.ShouldBeNull();
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>You are signed out</h1>");
        html.ShouldContain("Your session at Stub stays open, because Stub does not let Recall end it.");
        html.ShouldContain("sign in to Recall as you with no password");
        html.ShouldContain("sign out on the site of Stub, or delete the cookies of this browser");
        html.ShouldNotContain("Sign out of");
        plainVisit.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        plainVisit.Headers.Location!.OriginalString.ShouldStartWith("/auth/oidc/stub/start?client=web");
    }

    [Test]
    public async Task a_provider_that_is_down_at_the_logout_still_ends_the_session_and_the_page_says_what_is_not_known()
    {
        // Arrange
        var host = AuthHosts.DownAtLogout;
        using var browser = await SignedInBrowserAsync(host);
        var session = browser.SessionCookie!;
        host.Provider.Down = true;
        try
        {
            await WaitForDegradedAsync(host);

            // Act
            var logout = await LogoutAsync(browser);
            var page = await browser.GetAsync(SignedOutPage);
            var tryAgain = await browser.GetAsync("/auth/login?returnUrl=%2F");
            browser.SetCookie(browser.SessionCookieName, session);
            var me = await browser.GetAsync("/auth/me", csrf: true);

            // Assert
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            me.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var html = await page.Content.ReadAsStringAsync();
            html.ShouldContain("<h1>You are signed out</h1>");
            html.ShouldContain("Stub did not answer, so your session there can still be open.");
            html.ShouldContain("Wait a few minutes, then try again.");
            var tryAgainHtml = await tryAgain.Content.ReadAsStringAsync();
            tryAgainHtml.ShouldContain("<h1>You are signed out</h1>");
            tryAgainHtml.ShouldContain("Stub did not answer, so your session there can still be open.");
        }
        finally
        {
            host.Provider.Down = false;
        }
    }

    private static async Task WaitForDegradedAsync(AuthFlowFactory host)
    {
        using var probe = new AuthBrowser(host);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await probe.GetAsync("/auth/status");
            if ((await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString() == "degraded")
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The stack did not see the provider go down.");
    }
}
