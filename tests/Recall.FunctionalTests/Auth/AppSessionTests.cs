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
    [Arguments("0")]
    [Arguments("true")]
    [Arguments("")]
    [Arguments("2", "1")]
    [Arguments("1", "1")]
    public async Task a_cookie_with_another_value_of_the_csrf_header_is_refused(params string[] values)
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Me);
        request.Headers.TryAddWithoutValidation("X-CSRF", values);

        // Act
        var response = await browser.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("/auth/sessions/revoke-others")]
    [Arguments("/api/v1/tenants")]
    public async Task a_write_with_a_cookie_and_no_csrf_header_is_refused_and_changes_nothing(string path)
    {
        // Arrange
        var subject = NewSubject();
        using var browser = await SignedInBrowserAsync(subject);
        using var other = await SignedInBrowserAsync(subject);

        // Act
        var response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await other.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_cross_origin_page_gets_no_cors_answer_so_it_cannot_send_the_csrf_header()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        var preflight = new HttpRequestMessage(HttpMethod.Options, Me);
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-csrf");
        var read = new HttpRequestMessage(HttpMethod.Get, Me);
        read.Headers.Add("Origin", "https://evil.example");
        read.Headers.Add("X-CSRF", "1");

        // Act
        var preflightAnswer = await browser.SendAsync(preflight);
        var readAnswer = await browser.SendAsync(read);

        // Assert
        foreach (var answer in new[] { preflightAnswer, readAnswer })
        {
            answer.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
            answer.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        }
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
        asCookie.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
        (await other.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
    public async Task a_logout_with_the_cookie_of_an_ended_session_clears_it_and_says_signed_out()
    {
        // Arrange
        using var browser = NewBrowser();
        browser.SetCookie(browser.SessionCookieName, "rcl_ck_dead");

        // Act
        var response = await browser.PostFormAsync("/auth/logout", [], csrf: true);
        var page = await browser.GetAsync("/auth/login?signedOut=1");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.SessionCookie.ShouldBeNull();
        (await page.Content.ReadAsStringAsync()).ShouldContain("You are signed out");
    }

    [Test]
    public async Task a_cookie_logout_with_no_header_and_a_second_credential_ends_nothing_and_does_not_say_so()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());

        // Act
        var withBearer = await browser.PostFormAsync("/auth/logout", [], authorization: BearerOf(tokens.AccessToken));
        var withRefresh = await browser.PostFormAsync("/auth/logout", [("refresh_token", tokens.RefreshToken)]);

        // Assert
        withBearer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        withRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await phone.GetAsync(Me, authorization: BearerOf(tokens.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task the_basic_header_of_a_proxy_leaves_a_visitor_on_the_way_to_the_login()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync(Me, authorization: "Basic dXNlcjpwYXNz");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
        withHeader.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.SessionCookie.ShouldBeNull();
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
    public async Task me_has_the_shape_the_spa_and_the_phone_read()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var response = await browser.GetAsync(Me, csrf: true);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        me.EnumerateObject().Select(property => property.Name).ShouldBe(["sub", "name", "sid", "client_kind"], ignoreOrder: true);
        me.GetProperty("sub").GetString().ShouldStartWith("usr_");
        me.GetProperty("name").GetString().ShouldBe("Ada Lovelace");
        me.GetProperty("sid").GetString().ShouldNotBeNullOrEmpty();
        me.GetProperty("client_kind").GetString().ShouldBe("web");
    }

    [Test]
    public async Task me_says_no_content_to_a_visitor_with_no_session_and_401_to_a_credential_that_failed()
    {
        // Arrange
        using var signedIn = await SignedInBrowserAsync();
        using var anonymous = NewBrowser();
        using var expired = NewBrowser();
        expired.SetCookie(expired.SessionCookieName, "rcl_ck_dead");
        using var phone = NewBrowser();

        // Act
        var cookieWithNoHeader = await signedIn.GetAsync(Me);
        var noSession = await anonymous.GetAsync(Me, csrf: true);
        var noSessionNoHeader = await anonymous.GetAsync(Me);
        var deadCookie = await expired.GetAsync(Me, csrf: true);
        var deadBearer = await phone.GetAsync(Me, authorization: BearerOf("rcl_at_dead"));
        var notOurs = await phone.GetAsync(Me, authorization: BearerOf("a.provider.token"));

        // Assert
        cookieWithNoHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noSession.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        noSessionNoHeader.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deadCookie.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deadBearer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        deadBearer.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        notOurs.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_logout_with_no_cookie_clears_nothing_and_ends_nothing()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();
        using var crossSite = NewBrowser();

        // Act
        var response = await crossSite.PostFormAsync("/auth/logout", []);
        var alive = await browser.GetAsync(Me, csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_cookie_logout_with_no_header_keeps_the_session_and_does_not_say_signed_out()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var response = await browser.PostFormAsync("/auth/logout", []);
        var alive = await browser.GetAsync(Me, csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_get_cannot_end_a_session()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var response = await browser.GetAsync("/auth/logout", csrf: true);
        var alive = await browser.GetAsync(Me, csrf: true);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_real_sign_out_says_signed_out_and_the_next_page_shows_it()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        var logout = await browser.PostFormAsync("/auth/logout", [], csrf: true);
        var page = await browser.GetAsync("/auth/login?signedOut=1");
        var me = await browser.GetAsync(Me, csrf: true);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.SessionCookie.ShouldBeNull();
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>You are signed out</h1>");
        me.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task a_phone_logout_sets_no_mark_of_a_browser_sign_out()
    {
        // Arrange
        using var phone = NewBrowser();
        var tokens = await phone.LoginNativeAsync(NewSubject());
        var linesBefore = phone.SetCookieLines.Count;

        // Act
        var response = await phone.PostFormAsync("/auth/logout", [], authorization: BearerOf(tokens.AccessToken));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        phone.SetCookieLines.Count.ShouldBe(linesBefore);
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
}
