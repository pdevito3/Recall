namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Auth;
using Shouldly;

public class AppSessionTests
{
    private const string Me = "/auth/me";

    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";

    private static AuthBrowser NewBrowser() => new(AuthHosts.Open);

    private static async Task<AuthBrowser> SignedInBrowserAsync(string? subject = null)
    {
        var browser = NewBrowser();
        await browser.LoginWebAsync(subject ?? NewSubject());
        return browser;
    }

    private static string BearerOf(string token) => $"Bearer {token}";

    [Test]
    public async Task a_cookie_with_the_csrf_header_passes_and_with_no_header_is_refused()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var with = await browser.GetAsync(Me, csrf: true);
        var withoutOnAuth = await browser.GetAsync(Me);
        var withoutOnApi = await browser.GetAsync("/api/v1/tenants");

        // Assert
        with.StatusCode.ShouldBe(HttpStatusCode.OK);
        withoutOnAuth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        withoutOnApi.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_cookie_value_used_as_a_bearer_is_refused()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        using var other = NewBrowser();

        // Act
        var response = await other.GetAsync(Me, authorization: BearerOf(browser.SessionCookie!));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_bearer_value_used_as_a_cookie_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());
        using var other = NewBrowser();
        other.SetCookie(other.SessionCookieName, tokens.AccessToken);

        // Act
        var asCookie = await other.GetAsync(Me, csrf: true);
        var asBearer = await browser.GetAsync(Me, authorization: BearerOf(tokens.AccessToken));

        // Assert
        asCookie.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asBearer.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("Basic dXNlcjpwYXNz")]
    [Arguments("Negotiate abc")]
    public async Task another_authorization_scheme_does_not_hide_the_cookie(string authorization)
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var response = await browser.GetAsync(Me, csrf: true, authorization: authorization);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("bearer")]
    [Arguments("BEARER")]
    [Arguments("Bearer")]
    public async Task the_bearer_scheme_name_has_no_case(string scheme)
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());

        // Act
        var response = await browser.GetAsync(Me, authorization: $"{scheme} {tokens.AccessToken}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task me_says_who_the_session_is_for_each_kind()
    {
        // Arrange
        var subject = NewSubject();
        using var browser = await SignedInBrowserAsync(subject);
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(subject);

        // Act
        var web = await (await browser.GetAsync(Me, csrf: true)).Content.ReadFromJsonAsync<JsonElement>();
        var native = await (await phone.GetAsync(Me, authorization: BearerOf(tokens.AccessToken))).Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        web.GetProperty("sub").GetString().ShouldStartWith("usr_");
        web.GetProperty("name").GetString().ShouldBe("Ada Lovelace");
        web.GetProperty("client_kind").GetString().ShouldBe("web");
        native.GetProperty("sub").GetString().ShouldBe(web.GetProperty("sub").GetString());
        native.GetProperty("client_kind").GetString().ShouldBe("native");
        native.GetProperty("sid").GetString().ShouldNotBe(web.GetProperty("sid").GetString());
    }

    [Test]
    public async Task a_real_session_reads_the_tenant_row_of_its_user()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());

        // Act
        var byCookie = await browser.GetAsync("/api/v1/tenants", csrf: true);
        var byBearer = await phone.GetAsync("/api/v1/tenants", authorization: BearerOf(tokens.AccessToken));

        // Assert
        byCookie.StatusCode.ShouldBe(HttpStatusCode.OK);
        byBearer.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await byCookie.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().ShouldBe(1);
        (await byBearer.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task revoke_others_ends_each_other_session_of_the_user_only()
    {
        // Arrange
        var subject = NewSubject();
        using var kept = await SignedInBrowserAsync(subject);
        using var other = await SignedInBrowserAsync(subject);
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(subject);
        using var stranger = await SignedInBrowserAsync();

        // Act
        var response = await kept.PostFormAsync("/auth/sessions/revoke-others", [], csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await kept.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stranger.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phone.GetAsync(Me, authorization: BearerOf(tokens.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthBrowser.ErrorOf(await phone.RefreshAsync(tokens.RefreshToken))).ShouldBe("invalid_grant");
    }

    [Test]
    public async Task revoke_others_needs_a_session()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.PostFormAsync("/auth/sessions/revoke-others", []);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task logout_with_nothing_or_with_a_dead_value_still_answers_no_content()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var nothing = await browser.PostFormAsync("/auth/logout", []);
        var deadRefresh = await browser.PostFormAsync("/auth/logout", [("refresh_token", RefreshToken.New().Encode())]);
        var deadBearer = await browser.PostFormAsync("/auth/logout", [], authorization: BearerOf("rcl_at_dead"));
        var notAToken = await browser.PostFormAsync("/auth/logout", [("refresh_token", "nope")]);

        // Assert
        nothing.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deadRefresh.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deadBearer.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        notAToken.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task logout_with_a_bearer_ends_that_native_session()
    {
        // Arrange
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());

        // Act
        var response = await phone.PostFormAsync("/auth/logout", [], authorization: BearerOf(tokens.AccessToken));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await phone.GetAsync(Me, authorization: BearerOf(tokens.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task logout_with_only_a_refresh_token_ends_a_session_whose_access_token_is_gone()
    {
        // Arrange
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());

        // Act
        var response = await phone.PostFormAsync("/auth/logout", [("refresh_token", tokens.RefreshToken)]);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await phone.GetAsync(Me, authorization: BearerOf(tokens.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task logout_with_a_cookie_needs_the_csrf_header_to_end_the_session()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var cookie = browser.SessionCookie!;
        using var forged = NewBrowser();
        forged.SetCookie(forged.SessionCookieName, cookie);

        // Act
        var noHeader = await forged.PostFormAsync("/auth/logout", []);
        var aliveAfterForgery = await browser.GetAsync(Me, csrf: true);
        var withHeader = await browser.PostFormAsync("/auth/logout", [], csrf: true);
        forged.SetCookie(forged.SessionCookieName, cookie);
        var afterLogout = await forged.GetAsync(Me, csrf: true);

        // Assert
        noHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noHeader.Headers.Contains("Set-Cookie").ShouldBeFalse();
        forged.SessionCookie.ShouldBe(cookie);
        aliveAfterForgery.StatusCode.ShouldBe(HttpStatusCode.OK);
        withHeader.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        browser.SessionCookie.ShouldBeNull();
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_refresh_gives_a_new_pair_and_kills_the_old_access_token()
    {
        // Arrange
        using var phone = NewBrowser();
        var first = await phone.LoginNativeAsync(NewSubject());

        // Act
        var response = await phone.RefreshAsync(first.RefreshToken, Secrets.New());
        var second = await AuthBrowser.TokensOf(response);

        // Assert
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        second.AccessToken.ShouldNotBe(first.AccessToken);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.ExpiresIn.ShouldBeInRange(800, 900);
        (await phone.GetAsync(Me, authorization: BearerOf(first.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phone.GetAsync(Me, authorization: BearerOf(second.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_replay_revokes_the_whole_session()
    {
        // Arrange
        using var phone = NewBrowser();
        var first = await phone.LoginNativeAsync(NewSubject());
        var second = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken));

        // Act
        var replay = await phone.RefreshAsync(first.RefreshToken);

        // Assert
        (await AuthBrowser.ErrorOf(replay)).ShouldBe("invalid_grant");
        (await phone.GetAsync(Me, authorization: BearerOf(second.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthBrowser.ErrorOf(await phone.RefreshAsync(second.RefreshToken))).ShouldBe("invalid_grant");
    }

    [Test]
    public async Task a_retry_key_gets_the_same_pair_across_two_lost_responses()
    {
        // Arrange
        using var phone = NewBrowser();
        var first = await phone.LoginNativeAsync(NewSubject());
        var key = Secrets.New();

        // Act
        var lost = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, key));
        var lostAgain = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, key));
        var received = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, key));
        var next = await AuthBrowser.TokensOf(await phone.RefreshAsync(received.RefreshToken, Secrets.New()));

        // Assert
        lostAgain.AccessToken.ShouldBe(lost.AccessToken);
        lostAgain.RefreshToken.ShouldBe(lost.RefreshToken);
        received.AccessToken.ShouldBe(lost.AccessToken);
        received.RefreshToken.ShouldBe(lost.RefreshToken);
        (await phone.GetAsync(Me, authorization: BearerOf(next.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task copies_of_one_refresh_at_the_same_time_give_one_pair_and_no_revoke()
    {
        // Arrange
        using var phone = NewBrowser();
        var first = await phone.LoginNativeAsync(NewSubject());
        var key = Secrets.New();

        // Act
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, key))));

        // Assert
        answers.Select(answer => answer.RefreshToken).Distinct().ShouldHaveSingleItem();
        answers.Select(answer => answer.AccessToken).Distinct().ShouldHaveSingleItem();
        (await phone.GetAsync(Me, authorization: BearerOf(answers[0].AccessToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_copy_with_a_key_after_a_next_rotation_is_a_replay()
    {
        // Arrange
        using var phone = NewBrowser();
        var first = await phone.LoginNativeAsync(NewSubject());
        var key = Secrets.New();
        var second = await AuthBrowser.TokensOf(await phone.RefreshAsync(first.RefreshToken, key));
        var third = await AuthBrowser.TokensOf(await phone.RefreshAsync(second.RefreshToken, Secrets.New()));

        // Act
        var late = await phone.RefreshAsync(first.RefreshToken, key);

        // Assert
        (await AuthBrowser.ErrorOf(late)).ShouldBe("invalid_grant");
        (await phone.GetAsync(Me, authorization: BearerOf(third.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task bff_user_has_the_shape_the_spa_reads()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var response = await browser.GetAsync("/bff/user", csrf: true);
        var claims = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(claim => claim.GetProperty("type").GetString()!, claim => claim.GetProperty("value"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        claims["sub"].GetString().ShouldStartWith("usr_");
        claims["name"].GetString().ShouldBe("Ada Lovelace");
        claims["bff:logout_url"].GetString().ShouldBe($"/bff/logout?sid={claims["sid"].GetString()}");
        claims["bff:session_expires_in"].ValueKind.ShouldBe(JsonValueKind.Number);
        claims["bff:session_expires_in"].GetDouble().ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task bff_user_needs_the_csrf_header_and_answers_no_content_with_no_web_session()
    {
        // Arrange
        using var signedIn = await SignedInBrowserAsync();
        using var anonymous = NewBrowser();
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());

        // Act
        var noHeader = await signedIn.GetAsync("/bff/user");
        var noSession = await anonymous.GetAsync("/bff/user", csrf: true);
        var native = await phone.GetAsync("/bff/user", csrf: true, authorization: BearerOf(tokens.AccessToken));

        // Assert
        noHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noSession.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        native.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task bff_logout_with_no_cookie_clears_nothing_and_ends_nothing()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        using var crossSite = NewBrowser();

        // Act
        var response = await crossSite.GetAsync("/bff/logout");
        var alive = await browser.GetAsync(Me, csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task bff_logout_needs_the_session_id_and_then_ends_the_session()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var me = await (await browser.GetAsync(Me, csrf: true)).Content.ReadFromJsonAsync<JsonElement>();
        var sid = me.GetProperty("sid").GetString()!;

        // Act
        var wrong = await browser.GetAsync("/bff/logout?sid=other");
        var alive = await browser.GetAsync(Me, csrf: true);
        var right = await browser.GetAsync($"/bff/logout?sid={sid}");

        // Assert
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var wrongPage = await wrong.Content.ReadAsStringAsync();
        wrongPage.ShouldContain("<h1>This sign-out link is not valid</h1>");
        wrongPage.ShouldNotContain("sign-in request");
        wrongPage.ShouldContain(">Back to Recall</a>");
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
        right.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        right.Headers.Location!.OriginalString.ShouldBe("/auth/login?signedOut=1");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_real_sign_out_says_signed_out_and_the_next_page_shows_it()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var sid = (await (await browser.GetAsync(Me, csrf: true)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString()!;

        // Act
        var logout = await browser.GetAsync($"/bff/logout?sid={sid}");
        var page = await browser.GetAsync(logout.Headers.Location!.OriginalString);

        // Assert
        browser.SetCookieLines.Last().ShouldContain("samesite=strict", Case.Insensitive);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>You are signed out</h1>");
    }

    [Test]
    public async Task a_sign_out_from_another_site_with_the_real_sid_says_that_the_session_stays()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var sid = (await (await browser.GetAsync(Me, csrf: true)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString()!;
        using var crossSite = NewBrowser();

        // Act
        var response = await crossSite.GetAsync($"/bff/logout?sid={sid}");
        var alive = await browser.GetAsync(Me, csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var page = await response.Content.ReadAsStringAsync();
        page.ShouldContain("<h1>Sign-out did not finish</h1>");
        page.ShouldContain("This request ended no session");
        page.ShouldContain(">Back to Recall</a>");
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task the_signed_out_page_says_signed_out_only_to_a_browser_that_signed_out()
    {
        // Arrange
        using var stranger = NewBrowser();

        // Act
        var page = await stranger.GetAsync("/auth/login?signedOut=1");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldNotContain("You are signed out");
        html.ShouldContain("<h1>Sign in to Recall</h1>");
        html.ShouldContain("<title>Sign in to Recall</title>");
        html.ShouldContain("/auth/oidc/stub/start?client=web");
    }

    [Test]
    public async Task a_live_session_at_the_signed_out_page_goes_back_to_the_app()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var page = await browser.GetAsync("/auth/login?signedOut=1");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Test]
    public async Task a_sign_out_from_another_site_for_a_session_that_ended_says_nothing_false()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var sid = (await (await browser.GetAsync(Me, csrf: true)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString()!;
        await browser.GetAsync($"/bff/logout?sid={sid}");
        using var crossSite = NewBrowser();

        // Act
        var response = await crossSite.GetAsync($"/bff/logout?sid={sid}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Test]
    public async Task an_unknown_cookie_at_the_login_route_starts_a_new_login()
    {
        // Arrange
        using var browser = NewBrowser();
        browser.SetCookie(browser.SessionCookieName, "rcl_ck_dead");

        // Act
        var response = await browser.GetAsync("/auth/login");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldStartWith("/auth/oidc/stub/start?client=web");
    }

    [Test]
    public async Task me_with_no_session_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync(Me);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
