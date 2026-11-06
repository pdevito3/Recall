namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Shouldly;

public class AuthRefusalTests
{
    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";

    private static AuthBrowser NewBrowser() => new(AuthHosts.Open);

    [Test]
    public async Task a_callback_with_a_state_that_no_attempt_has_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();
        var code = browser.Provider.Approve(authorize, new StubLogin(NewSubject()));
        var state = Secrets.New();
        browser.SetCookie($"recall_login_{TokenHash.Of(state).Hex[..8]}", Secrets.New(), "/auth/oidc");

        // Act
        var response = await browser.CallbackAsync(state, code);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_callback_with_no_state_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/oidc/callback?code=abc");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task a_state_works_once()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();
        var binding = browser.Cookies.Single(cookie => cookie.Key.StartsWith("recall_login_", StringComparison.Ordinal));
        var first = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));
        first.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.DropCookie(browser.SessionCookieName);
        browser.SetCookie(binding.Key, binding.Value.Value, binding.Value.Path);

        // Act
        var second = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task an_expired_attempt_is_refused_and_a_live_one_with_the_same_shape_passes()
    {
        // Arrange
        using var expired = NewBrowser();
        using var live = NewBrowser();
        var expiredLogin = await PlantAttemptAsync(expired, TimeSpan.FromSeconds(-1));
        var liveLogin = await PlantAttemptAsync(live, TimeSpan.FromMinutes(5));

        // Act
        var refused = await expired.FinishAtProviderAsync(expiredLogin, new StubLogin(NewSubject()));
        var passed = await live.FinishAtProviderAsync(liveLogin, new StubLogin(NewSubject()));

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        expired.SessionCookie.ShouldBeNull();
        passed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        live.SessionCookie.ShouldNotBeNull();
    }

    private static async Task<AuthorizeRequest> PlantAttemptAsync(AuthBrowser browser, TimeSpan expiresIn)
    {
        var state = Secrets.New();
        var nonce = Secrets.New();
        var binding = Secrets.New();
        var verifier = Secrets.New();
        var hash = TokenHash.Of(state);
        const string redirectUri = "http://localhost/auth/oidc/callback";
        await AuthHosts.Open.WithStoreAsync(async store =>
        {
            await store.AddAttemptAsync(new LoginAttempt
            {
                StateHash = hash.Hex,
                BindingHash = TokenHash.Of(binding).Hex,
                ModeId = AuthFlowFactory.ModeId,
                Nonce = nonce,
                PkceVerifier = verifier,
                RedirectUri = redirectUri,
                ClientKind = SessionKind.Web,
                ReturnPath = "/",
                ExpiresAt = TimeProvider.System.GetUtcNow() + expiresIn,
            }, CancellationToken.None);
            return true;
        });
        browser.SetCookie($"recall_login_{hash.Hex[..8]}", binding, "/auth/oidc");
        return new AuthorizeRequest(state, nonce, Pkce.ChallengeOf(verifier), redirectUri, StubOidcProvider.ClientId);
    }

    [Test]
    public async Task a_callback_with_the_wrong_binding_cookie_is_refused_and_does_not_burn_the_attempt()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();
        var binding = browser.Cookies.Single(cookie => cookie.Key.StartsWith("recall_login_", StringComparison.Ordinal));
        browser.SetCookie(binding.Key, Secrets.New(), binding.Value.Path);

        // Act
        var refused = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));
        browser.SetCookie(binding.Key, binding.Value.Value, binding.Value.Path);
        var passed = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        passed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task a_callback_in_a_browser_with_no_binding_cookie_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        using var other = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await other.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        other.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task an_id_token_with_another_nonce_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { Nonce = Secrets.New() });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task an_id_token_from_another_issuer_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { IdTokenIssuer = "https://other.test" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_token_answer_that_cannot_be_decompressed_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { TokenBodyIsCorrupt = true });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_token_answer_with_a_lone_surrogate_in_the_id_token_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { TokenBody = """{"id_token":"\ud800"}""" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task an_access_token_that_no_header_can_carry_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { AccessToken = "two\nlines" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_userinfo_answer_that_cannot_be_decompressed_leaves_the_login_on_the_id_token()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoBodyIsCorrupt = true });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public async Task a_userinfo_answer_with_a_lone_surrogate_leaves_the_login_on_the_id_token()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoBody = """{"sub":"\ud800"}""" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public async Task a_userinfo_answer_of_another_person_is_refused_also_with_a_claim_that_cannot_be_read()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoBody = """{"sub":"another-person","name":"\ud800"}""" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Arguments("bogus")]
    [Arguments("utf-7")]
    public async Task a_token_answer_with_a_charset_that_cannot_be_read_is_refused(string charset)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { TokenCharset = charset });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Arguments("bogus")]
    [Arguments("utf-7")]
    public async Task a_userinfo_answer_with_a_charset_that_cannot_be_read_leaves_the_login_on_the_id_token(string charset)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoCharset = charset });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    [Arguments("[]")]
    [Arguments("\"text\"")]
    [Arguments("{}")]
    [Arguments("not json")]
    public async Task a_token_answer_with_no_id_token_in_an_object_is_refused(string tokenBody)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { TokenBody = tokenBody });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_userinfo_with_another_subject_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoSubject = NewSubject() });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Arguments("https://other.test")]
    [Arguments(null)]
    public async Task an_authorization_response_that_does_not_name_this_issuer_is_refused(string? iss)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()), iss);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Arguments("https://evil.test/callback")]
    [Arguments("https://evil.test")]
    [Arguments("recall://auth/other")]
    [Arguments("recall://evil/callback")]
    [Arguments("RECALL://auth/callback")]
    [Arguments("recall://auth/callback?x=1")]
    [Arguments("recall://auth/callback#x")]
    [Arguments("other://auth/callback")]
    [Arguments("http://localhost:7000/cb")]
    [Arguments("http://127.0.0.1/cb")]
    [Arguments("http://127.0.0.1:0/cb")]
    [Arguments("http://127.0.0.1:65536/cb")]
    [Arguments("http://127.0.0.1:7000")]
    [Arguments("http://127.0.0.1:7000/cb?x=1")]
    [Arguments("http://127.0.0.1:7000/cb#x")]
    [Arguments("http://user@127.0.0.1:7000/cb")]
    [Arguments("http://127.0.0.1.evil.test:7000/cb")]
    [Arguments("http://[::2]:7000/cb")]
    [Arguments("https://127.0.0.1:7000/cb")]
    public async Task a_native_start_with_a_redirect_of_another_form_is_refused_with_no_redirect(string redirect)
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync(NativeApp.New(redirect).StartUrl);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Test]
    [Arguments("recall://auth/callback")]
    [Arguments("http://127.0.0.1:7000/cb")]
    [Arguments("http://[::1]:7000/cb")]
    public async Task a_native_start_with_an_allowed_redirect_goes_to_the_provider(string redirect)
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync(NativeApp.New(redirect).StartUrl);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task an_app_code_works_once()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var code = await browser.PendingCodeAsync(app, NewSubject());
        await AuthBrowser.TokensOf(await browser.ExchangeAsync(code, app.Verifier, app.Redirect));

        // Act
        var second = await browser.ExchangeAsync(code, app.Verifier, app.Redirect);

        // Assert
        (await AuthBrowser.ErrorOf(second)).ShouldBe("invalid_grant");
    }

    [Test]
    public async Task a_token_request_with_another_redirect_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var code = await browser.PendingCodeAsync(app, NewSubject());

        // Act
        var response = await browser.ExchangeAsync(code, app.Verifier, "http://127.0.0.1:7000/cb");

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_grant");
    }

    [Test]
    public async Task a_token_request_with_the_wrong_verifier_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var code = await browser.PendingCodeAsync(app, NewSubject());

        // Act
        var response = await browser.ExchangeAsync(code, Secrets.New(), app.Redirect);

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_grant");
    }

    [Test]
    public async Task a_token_request_for_a_user_who_is_not_live_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var tokens = await browser.LoginNativeAsync(NewSubject());
        var me = await browser.GetAsync("/auth/me", authorization: $"Bearer {tokens.AccessToken}");
        var userId = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sub").GetString()!;
        await DeleteUserAsync(userId);
        var code = await AuthHosts.Open.WithStoreAsync(store =>
            store.AddAppCodeAsync(userId, AuthFlowFactory.ModeId, app.Redirect, app.Challenge, app.ClientState, CancellationToken.None));

        // Act
        var response = await browser.ExchangeAsync(code, app.Verifier, app.Redirect);

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_grant");
    }

    private static Task<int> DeleteUserAsync(string userId)
        => AuthHosts.Open.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(row => row.Id == userId);
            user.Delete();
            database.Users.Remove(user);
            return await database.SaveChangesAsync();
        });

    [Test]
    public async Task a_refresh_token_that_was_replaced_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());
        await AuthBrowser.TokensOf(await browser.RefreshAsync(tokens.RefreshToken));

        // Act
        var replay = await browser.RefreshAsync(tokens.RefreshToken);

        // Assert
        (await AuthBrowser.ErrorOf(replay)).ShouldBe("invalid_grant");
    }

    [Test]
    [Arguments("https://evil.test/")]
    [Arguments("//evil.test/")]
    [Arguments("/\\evil.test")]
    [Arguments("\\\\evil.test")]
    [Arguments("/\t/evil.test")]
    [Arguments("javascript:alert(1)")]
    [Arguments("evil.test")]
    public async Task an_open_redirect_in_the_return_url_goes_to_the_root_after_a_full_login(string returnUrl)
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync(returnUrl);

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Test]
    public async Task a_local_return_url_survives_a_full_login()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync("/entries/1?tab=2");

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        response.Headers.Location!.OriginalString.ShouldBe("/entries/1?tab=2");
    }

    [Test]
    [Arguments("/auth/login?returnUrl=https%3A%2F%2Fevil.test%2F", "/auth/oidc/stub/start?client=web&returnUrl=%2F")]
    [Arguments("/auth/login?returnUrl=%2F%2Fevil.test", "/auth/oidc/stub/start?client=web&returnUrl=%2F")]
    public async Task an_open_redirect_in_the_login_route_is_dropped(string url, string expected)
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync(url);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(expected);
    }

    [Test]
    public async Task a_signed_in_person_at_the_login_route_goes_only_to_a_local_path()
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());

        // Act
        var response = await browser.GetAsync("/auth/login?returnUrl=%2F%2Fevil.test");

        // Assert
        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Test]
    [Arguments("state")]
    [Arguments("code_challenge")]
    [Arguments("redirect_uri")]
    public async Task a_native_start_with_a_value_over_its_limit_is_refused(string field)
    {
        // Arrange
        using var browser = NewBrowser();
        var app = NativeApp.New();
        var tooLong = new string('a', 600);
        var values = new Dictionary<string, string>
        {
            ["state"] = field == "state" ? tooLong : app.ClientState,
            ["code_challenge"] = field == "code_challenge" ? tooLong : app.Challenge,
            ["redirect_uri"] = field == "redirect_uri" ? $"http://127.0.0.1:7000/{tooLong}" : app.Redirect,
        };
        var query = string.Join('&', values.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        // Act
        var response = await browser.GetAsync($"/auth/oidc/stub/start?client=app&code_challenge_method=S256&{query}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task a_return_url_over_its_limit_goes_to_the_root()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync("/" + new string('a', LocalReturnPathLimit));

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()));

        // Assert
        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    private const int LocalReturnPathLimit = 2048;

    [Test]
    [Arguments("")]
    [Arguments("short")]
    public async Task a_refresh_with_a_weak_retry_key_is_invalid_request_with_no_write(string retryKey)
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());

        // Act
        var refused = await browser.RefreshAsync(tokens.RefreshToken, retryKey);
        var passed = await browser.RefreshAsync(tokens.RefreshToken);

        // Assert
        (await AuthBrowser.ErrorOf(refused)).ShouldBe("invalid_request");
        passed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_refresh_with_two_retry_keys_is_invalid_request_with_no_write()
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());
        var key = Convert.ToHexString(new byte[32]);

        // Act
        var refused = await browser.PostFormAsync(
            "/auth/token", [("grant_type", "refresh_token"), ("refresh_token", tokens.RefreshToken), ("retry_key", key), ("retry_key", key)]);
        var passed = await browser.RefreshAsync(tokens.RefreshToken);

        // Assert
        (await AuthBrowser.ErrorOf(refused)).ShouldBe("invalid_request");
        passed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_refresh_with_no_token_is_invalid_request()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var refused = await browser.PostFormAsync("/auth/token", [("grant_type", "refresh_token")]);

        // Assert
        (await AuthBrowser.ErrorOf(refused)).ShouldBe("invalid_request");
    }

    [Test]
    [Arguments("application/x-www-form-urlencoded-x")]
    [Arguments("application/x-www-form-urlencodedfoo")]
    [Arguments("not a type")]
    public async Task a_token_request_of_another_content_type_is_refused(string contentType)
    {
        // Arrange
        using var browser = NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
        {
            Content = new ByteArrayContent("grant_type=refresh_token"u8.ToArray()),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        // Act
        var response = await browser.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task a_userinfo_with_no_subject_adds_nothing_to_the_person()
    {
        // Arrange
        using var browser = NewBrowser();
        var authorize = await browser.StartWebAsync();

        // Act
        await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()) { UserinfoOmitsSubject = true, UserinfoName = "From Userinfo" });
        var me = await (await browser.GetAsync("/auth/me", csrf: true)).Content.ReadAsStringAsync();

        // Assert
        browser.SessionCookie.ShouldNotBeNull();
        me.ShouldNotContain("From Userinfo");
    }

    [Test]
    public async Task a_token_body_over_its_limit_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.PostFormAsync("/auth/token", [("grant_type", new string('a', 17 * 1024))]);

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_request");
    }

    [Test]
    public async Task a_token_request_that_is_not_a_form_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { grant_type = "refresh_token" })),
        };

        // Act
        var response = await browser.SendAsync(request);

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_request");
    }

    [Test]
    public async Task an_unknown_mode_at_start_is_not_found()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/oidc/nope/start?client=web");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task a_start_with_an_unknown_client_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/oidc/stub/start?client=other");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
