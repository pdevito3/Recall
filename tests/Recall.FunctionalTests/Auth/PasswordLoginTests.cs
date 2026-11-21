namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Shouldly;

public class PasswordLoginTests
{
    private const string HeldHashSlot = "held hash slot";

    private static AuthFlowFactory Host => AuthHosts.Password;

    /// <summary>An address in a /64 of its own, so the wait of one test does not reach another.</summary>
    private static string NewAddress() => $"2001:db8:{Random.Shared.Next(1, 0xffff):x}:{Random.Shared.Next(1, 0xffff):x}::1";

    private static async Task<string> TextOf(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    [Test]
    [Property("Rule", "PWD-1")]
    public async Task with_no_provider_the_status_lists_the_password_mode_and_the_login_page_shows_its_form()
    {
        // Arrange
        using var browser = new AuthBrowser(Host);

        // Act
        var status = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();
        var login = await browser.GetAsync("/auth/login");

        // Assert
        status.GetProperty("state").GetString().ShouldBe("ready");
        var mode = status.GetProperty("modes").EnumerateArray().ShouldHaveSingleItem();
        mode.GetProperty("kind").GetString().ShouldBe("password");
        mode.GetProperty("startUrl").GetString().ShouldBe("/auth/login?client=app");
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await TextOf(login);
        page.ShouldContain("autocomplete=\"username\"");
        page.ShouldContain("autocomplete=\"current-password\"");
    }

    [Test]
    [Property("Rule", "PWD-1")]
    public async Task with_a_provider_and_a_password_user_the_login_page_shows_both_and_starts_nothing_with_no_click()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Mixed);

        // Act
        var status = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();
        var login = await browser.GetAsync("/auth/login");

        // Assert
        status.GetProperty("modes").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).ShouldBe(["password", "oidc"]);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await TextOf(login);
        page.ShouldContain("action=\"/auth/password/login\"");
        page.ShouldContain("Sign in with Stub");
    }

    [Test]
    [Property("Rule", "PWD-1")]
    public async Task with_a_provider_and_no_password_user_a_single_provider_still_starts_with_no_click()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Closed);

        // Act
        var status = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();
        var login = await browser.GetAsync("/auth/login");

        // Assert
        status.GetProperty("modes").EnumerateArray().ShouldHaveSingleItem().GetProperty("kind").GetString().ShouldBe("oidc");
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.ShouldStartWith($"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=web");
    }

    [Test]
    [Property("Rule", "PWD-7")]
    public async Task a_right_password_gives_a_web_session_and_goes_to_the_return_path()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);
        var state = await browser.FormStateAsync("/auth/login?returnUrl=%2Fentries");

        // Act
        var response = await browser.PostLoginAsync(state, person.LoginName.ToUpperInvariant(), person.Password);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/entries");
        browser.SessionCookie.ShouldNotBeNull();
        (await browser.UserOfSessionAsync()).ShouldBe(person.UserId);
    }

    [Test]
    [Property("Rule", "PWD-7")]
    public async Task a_native_password_login_redirects_to_the_app_with_a_code_and_no_confirm_page()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);
        var app = NativeApp.New();
        var loginUrl = $"/auth/login?client=app&redirect_uri={Uri.EscapeDataString(app.Redirect)}&code_challenge={app.Challenge}&code_challenge_method=S256&state={app.ClientState}";
        var state = await browser.FormStateAsync(loginUrl);

        // Act
        var response = await browser.PostLoginAsync(state, person.LoginName, person.Password);
        var location = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        var tokens = await AuthBrowser.TokensOf(await browser.ExchangeAsync(query["code"].ToString(), app.Verifier, app.Redirect));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        location.GetLeftPart(UriPartial.Path).ShouldBe(app.Redirect);
        query["state"].ToString().ShouldBe(app.ClientState);
        browser.SessionCookie.ShouldBeNull();
        var me = await browser.GetAsync("/auth/me", authorization: $"Bearer {tokens.AccessToken}");
        (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sub").GetString().ShouldBe(person.UserId);
    }

    [Test]
    [Property("Rule", "PWD-7")]
    public async Task the_token_route_has_no_password_grant()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);

        // Act
        var response = await browser.PostFormAsync("/auth/token", [("grant_type", "password"), ("username", person.LoginName), ("password", person.Password)]);

        // Assert
        (await AuthBrowser.ErrorOf(response)).ShouldBe("invalid_request");
    }

    [Test]
    [Property("Rule", "PWD-6")]
    public async Task a_wrong_password_an_unknown_name_and_a_stopped_account_each_run_one_hash_and_an_unknown_name_answers_as_a_wrong_password()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        var stopped = await PasswordPeople.AddAsync(Host, wrongTries: PasswordCredential.StopAt);
        var unknownName = $"nobody-{Guid.NewGuid():N}";
        var (wrongPassword, unknownPassword, stoppedPassword) = ($"wrong {Guid.NewGuid():N}", $"unknown {Guid.NewGuid():N}", $"stopped {Guid.NewGuid():N}");
        using var browser = new AuthBrowser(Host);

        // Act
        var wrong = await browser.SignInAsync(person.LoginName, wrongPassword, NewAddress());
        var unknown = await browser.SignInAsync(unknownName, unknownPassword, NewAddress());
        var stoppedAnswer = await browser.SignInAsync(stopped.LoginName, stoppedPassword, NewAddress());

        // Assert
        (wrong.StatusCode, unknown.StatusCode, stoppedAnswer.StatusCode).ShouldBe((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized));
        (Host.Hasher.ChecksOf(wrongPassword), Host.Hasher.ChecksOf(unknownPassword), Host.Hasher.ChecksOf(stoppedPassword)).ShouldBe((1, 1, 1));
        var wrongPage = await TextOf(wrong);
        wrongPage.ShouldContain(PasswordLogin.WrongMessage);
        PageWithout(await TextOf(unknown), unknownName).ShouldBe(PageWithout(wrongPage, person.LoginName));
        (await TextOf(stoppedAnswer)).ShouldContain(PasswordLogin.StoppedMessage);
        browser.SessionCookie.ShouldBeNull();
    }

    /// <summary>The page with its state and its typed login name taken out, the only values that differ from one answer to the next.</summary>
    private static string PageWithout(string page, string typedName)
        => page.Replace(PasswordPeople.StateIn(page), "STATE", StringComparison.Ordinal).Replace(typedName, "NAME", StringComparison.Ordinal);

    [Test]
    [Property("Rule", "PWD-5")]
    public async Task a_post_with_no_attempt_another_browsers_attempt_or_a_used_attempt_gets_the_refused_page()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);
        using var other = new AuthBrowser(Host);
        var state = await browser.FormStateAsync();
        var used = await browser.FormStateAsync();
        (await browser.PostLoginAsync(used, person.LoginName, "a wrong password for this person")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Act
        var noAttempt = await browser.PostLoginAsync("no-such-state", person.LoginName, person.Password);
        var otherBrowser = await other.PostLoginAsync(state, person.LoginName, person.Password);
        var usedAgain = await browser.PostLoginAsync(used, person.LoginName, person.Password);

        // Assert
        foreach (var refused in new[] { noAttempt, otherBrowser, usedAgain })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await TextOf(refused)).ShouldContain("This sign-in is not valid");
        }

        browser.SessionCookie.ShouldBeNull();
        other.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-8")]
    public async Task a_form_body_over_8_kib_is_refused()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);
        var state = await browser.FormStateAsync();

        // Act
        var response = await browser.PostLoginAsync(state, person.LoginName, person.Password + new string('x', 9 * 1024));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-10")]
    public async Task a_login_with_a_hash_of_a_lower_cost_stores_a_new_hash()
    {
        // Arrange
        const string password = "an old password with a weak hash";
        var oldHash = PasswordPeople.HashOf(password, iterations: 10_000);
        var person = await PasswordPeople.AddAsync(Host, hash: oldHash, password: password);
        using var browser = new AuthBrowser(Host);

        // Act
        var response = await browser.SignInAsync(person.LoginName, password);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var stored = (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash;
        stored.ShouldNotBe(oldHash);
        new PasswordHasher<PasswordCredential>(Options.Create(new PasswordHasherOptions { IterationCount = PasswordHashing.IterationCount }))
            .VerifyHashedPassword(null!, stored, password).ShouldBe(PasswordVerificationResult.Success);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public async Task after_5_wrong_tries_the_pair_waits_and_another_address_does_not()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        var address = NewAddress();
        using var browser = new AuthBrowser(Host);
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            (await browser.SignInAsync(person.LoginName, "a wrong password for this person", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Act
        var waits = await browser.SignInAsync(person.LoginName.ToUpperInvariant(), person.Password, address);
        var otherAddress = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());

        // Assert
        waits.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        waits.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 30);
        (await TextOf(waits)).ShouldContain("Too many wrong tries. Wait 1 minute, then try again.");
        otherAddress.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public async Task a_wait_of_one_name_leaves_another_name_from_the_same_address_free()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        var address = NewAddress();
        var other = $"nobody-{Guid.NewGuid():N}";
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            (await new AuthBrowser(Host).SignInAsync(other, "a wrong password for nobody here", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await new AuthBrowser(Host).SignInAsync(other, "a wrong password for nobody here", address)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act
        var response = await new AuthBrowser(Host).SignInAsync(person.LoginName, person.Password, address);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public async Task a_right_password_resets_the_pair_so_the_count_starts_again()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        var address = NewAddress();
        for (var i = 0; i < PasswordBackoff.FreeTries - 1; i++)
            (await new AuthBrowser(Host).SignInAsync(person.LoginName, "a wrong password for this person", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await new AuthBrowser(Host).SignInAsync(person.LoginName, person.Password, address)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await new AuthBrowser(Host).SignInAsync(person.LoginName, "a wrong password for this person", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Act
        // With no reset, this pair has 5 wrong tries in a row now, and this try waits.
        var response = await new AuthBrowser(Host).SignInAsync(person.LoginName, person.Password, address);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public async Task a_try_that_waits_runs_no_hash()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        var address = NewAddress();
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            (await new AuthBrowser(Host).SignInAsync(person.LoginName, "a wrong password for this person", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var waited = $"a try that waits {Guid.NewGuid():N}";

        // Act
        var response = await new AuthBrowser(Host).SignInAsync(person.LoginName, waited, address);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        Host.Hasher.ChecksOf(waited).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public async Task an_unknown_name_waits_the_same_as_a_known_one()
    {
        // Arrange
        var name = $"nobody-{Guid.NewGuid():N}";
        var address = NewAddress();
        using var browser = new AuthBrowser(Host);
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            (await browser.SignInAsync(name, "a wrong password for nobody here", address)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Act
        var waits = await browser.SignInAsync(name, "a wrong password for nobody here", address);

        // Assert
        waits.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    [Property("Rule", "PWD-12")]
    [Arguments(4, 0)]
    [Arguments(5, 30)]
    [Arguments(6, 60)]
    [Arguments(9, 480)]
    [Arguments(10, 900)]
    [Arguments(50, 900)]
    public void the_wait_starts_at_30_seconds_and_doubles_to_15_minutes(int wrongTries, int seconds)
    {
        // Act & Assert
        PasswordBackoff.WaitAfter(wrongTries).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Test]
    [Property("Rule", "PWD-13")]
    public async Task the_100th_wrong_try_stops_the_account_and_then_the_right_password_is_refused()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, wrongTries: PasswordCredential.StopAt - 1);
        using var browser = new AuthBrowser(Host);

        // Act
        var hundredth = await browser.SignInAsync(person.LoginName, "a wrong password for this person", NewAddress());
        var right = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());

        // Assert
        hundredth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(hundredth)).ShouldContain(PasswordLogin.StoppedMessage);
        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(right)).ShouldContain(PasswordLogin.StoppedMessage);
        browser.SessionCookie.ShouldBeNull();
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Stopped);
    }

    [Test]
    [Property("Rule", "PWD-13")]
    public async Task a_right_password_resets_the_count_of_wrong_tries()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, wrongTries: 42);
        using var browser = new AuthBrowser(Host);

        // Act
        var response = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).FailedCount.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-15")]
    public async Task an_expired_temporary_password_gets_the_expired_message_and_a_wrong_one_the_usual_message()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow - TimeSpan.FromDays(8));
        using var browser = new AuthBrowser(Host);

        // Act
        var right = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        var wrong = await browser.SignInAsync(person.LoginName, "a wrong password for this person", NewAddress());

        // Assert
        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(right)).ShouldContain(PasswordLogin.ExpiredMessage);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(wrong)).ShouldContain(PasswordLogin.WrongMessage);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-16")]
    public async Task a_right_temporary_password_opens_only_the_choose_page_and_the_choice_signs_in()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow);
        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        choosePage.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.SessionCookie.ShouldBeNull();
        var chooseState = PasswordPeople.StateIn(await TextOf(choosePage));
        var asLogin = await browser.PostLoginAsync(chooseState, person.LoginName, person.Password);
        asLogin.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var again = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        var sameAsTemporary = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(again))), ("password", person.Password)]);
        sameAsTemporary.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await TextOf(sameAsTemporary)).ShouldContain(PasswordLogin.SameAsTemporaryMessage);
        const string chosen = "a quiet tram ride to the old harbour";

        // Act
        var choice = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(sameAsTemporary))), ("password", chosen)]);

        // Assert
        choice.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await browser.UserOfSessionAsync()).ShouldBe(person.UserId);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Chosen);
        var actions = await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.RootId == person.UserId && e.Action == "password_chosen").CountAsync());
        actions.ShouldBe(1);
        using var later = new AuthBrowser(Host);
        (await later.SignInAsync(person.LoginName, chosen, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    [Property("Rule", "PWD-16")]
    public async Task a_chosen_password_follows_the_password_rules()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow);
        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());

        // Act
        var tooShort = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(choosePage))), ("password", "too short")]);

        // Assert
        tooShort.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await TextOf(tooShort)).ShouldContain(PasswordPolicy.TooShortMessage);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Temporary);
    }

    [Test]
    [Property("Rule", "PWD-23")]
    public async Task a_logout_of_a_password_session_ends_it_with_no_step_at_a_provider()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Mixed);
        var person = AuthHosts.MixedPerson;
        (await browser.SignInAsync(person.LoginName, person.Password, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Act
        var logout = await browser.PostFormAsync("/auth/logout", [], csrf: true);
        var me = await browser.GetAsync("/auth/me", csrf: true);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TextOf(logout)).ShouldBeEmpty();
        browser.SessionCookie.ShouldBeNull();
        me.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    [Property("Rule", "PWD-20")]
    public async Task no_password_or_hash_reaches_a_log_or_an_audit_value()
    {
        // Arrange
        var marker = $"marker{Guid.NewGuid():N}";
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, sameTenantAs: admin);
        using var adminBrowser = await SignedInAsync(admin);
        var issue = await adminBrowser.IssueTemporaryAsync(admin, person.UserId);
        issue.StatusCode.ShouldBe(HttpStatusCode.OK);
        var temporary = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("password").GetString()!;
        using var browser = new AuthBrowser(Host);
        var chosen = $"chosen {marker} words for later";

        // Act
        await browser.SignInAsync(person.LoginName, $"wrong {marker} words here", NewAddress());
        var choosePage = await browser.SignInAsync(person.LoginName, temporary, NewAddress());
        var choice = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(choosePage))), ("password", chosen)]);
        var hash = (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash;

        // Assert
        choice.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Host.Logs.Holding(person.UserId).ShouldContain(line => line.MessageTemplate.Text == "User {UserId} chose a password");
        Host.Logs.Holding(person.UserId).ShouldContain(line => line.MessageTemplate.Text.StartsWith("Issued a temporary password", StringComparison.Ordinal));
        Host.Logs.Holding(marker).ShouldBeEmpty();
        Host.Logs.Holding(temporary).ShouldBeEmpty();
        Host.Logs.Holding(hash).ShouldBeEmpty();
        var audit = await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters().Where(e => e.RootId == person.UserId).Select(e => e.Data).ToListAsync());
        audit.ShouldNotBeEmpty();
        audit.ShouldAllBe(data => !data.Contains(marker) && !data.Contains(temporary) && !data.Contains(hash));
    }

    [Test]
    [Property("Rule", "PWD-20")]
    public async Task the_setup_code_reaches_only_its_boot_line_and_the_setup_password_no_log()
    {
        // Arrange
        var host = AuthHosts.SetupLogs;
        var code = host.Services.GetRequiredService<SetupCode>().Current!;
        var marker = $"marker{Guid.NewGuid():N}";
        using var browser = new AuthBrowser(host);
        List<(string, string)> Fields(string state, string typedCode) =>
            [("state", state), ("code", typedCode), ("name", "Ada Lovelace"), ("loginName", "ada"), ("password", $"setup {marker} password words")];

        // Act
        var wrong = await browser.PostAsync("/auth/password/setup", Fields(await browser.FormStateAsync("/auth/password/setup"), $"WRONG{marker}"));
        var setup = await browser.PostAsync("/auth/password/setup", Fields(await browser.FormStateAsync("/auth/password/setup"), code));

        // Assert
        (wrong.StatusCode, setup.StatusCode).ShouldBe((HttpStatusCode.Forbidden, HttpStatusCode.Redirect));
        var owner = await host.WithDatabaseAsync(db => db.Users.IgnoreQueryFilters().SingleAsync());
        host.Logs.Holding(owner.Id).ShouldContain(line => line.MessageTemplate.Text.StartsWith("The setup made tenant", StringComparison.Ordinal));
        host.Logs.Holding(code).ShouldHaveSingleItem().MessageTemplate.Text.ShouldStartWith("This Recall server is not set up");
        host.Logs.Holding(marker).ShouldBeEmpty();
        var hash = (await PasswordPeople.CredentialOfAsync(host, owner.Id)).PasswordHash;
        host.Logs.Holding(hash).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task a_login_that_loaded_the_credential_before_an_issue_mints_no_session_after_it()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, sameTenantAs: admin);
        using var adminBrowser = await SignedInAsync(admin);
        using var browser = new AuthBrowser(Host);
        var hold = Host.Hasher.HoldNextCheckOf(person.Password);

        // Act
        var login = browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        await hold.ReachedAsync();
        var issue = await adminBrowser.IssueTemporaryAsync(admin, person.UserId);
        hold.Release();
        var response = await login;

        // Assert
        issue.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(response)).ShouldContain(PasswordLogin.WrongMessage);
        browser.SessionCookie.ShouldBeNull();
        var sessions = await Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == person.UserId));
        sessions.ShouldBe(0);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Temporary);
    }

    // A held check keeps one of the two hash slots, so only one such test runs at a time and the issue gets the other.
    [NotInParallel(HeldHashSlot)]
    [Test]
    [Property("Rule", "PWD-16")]
    public async Task a_choice_that_loaded_the_credential_before_an_issue_keeps_the_new_temporary_password()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow, sameTenantAs: admin);
        using var adminBrowser = await SignedInAsync(admin);
        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        var chosen = $"a chosen password that lost its race {Guid.NewGuid():N}";
        var hold = Host.Hasher.HoldNextCheckOf(chosen);

        // Act
        var choice = browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(choosePage))), ("password", chosen)]);
        await hold.ReachedAsync();
        var issue = await adminBrowser.IssueTemporaryAsync(admin, person.UserId);
        hold.Release();
        var response = await choice;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TextOf(response)).ShouldContain("This sign-in is not valid");
        browser.SessionCookie.ShouldBeNull();
        var credential = await PasswordPeople.CredentialOfAsync(Host, person.UserId);
        credential.State.ShouldBe(PasswordState.Temporary);
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();
        using var later = new AuthBrowser(Host);
        (await later.SignInAsync(person.LoginName, issued.GetProperty("password").GetString()!, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var old = new AuthBrowser(Host);
        (await old.SignInAsync(person.LoginName, chosen, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A held check keeps one of the two hash slots, so only one such test runs at a time and the issue gets the other.
    [NotInParallel(HeldHashSlot)]
    [Test]
    [Property("Rule", "PWD-13")]
    public async Task a_wrong_try_that_loaded_the_credential_before_an_issue_does_not_stop_the_new_temporary_password()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, wrongTries: PasswordCredential.StopAt - 1, sameTenantAs: admin);
        using var adminBrowser = await SignedInAsync(admin);
        using var browser = new AuthBrowser(Host);
        var wrong = $"the 100th wrong try {Guid.NewGuid():N}";
        var hold = Host.Hasher.HoldNextCheckOf(wrong);

        // Act
        var login = browser.SignInAsync(person.LoginName, wrong, NewAddress());
        await hold.ReachedAsync();
        var issue = await adminBrowser.IssueTemporaryAsync(admin, person.UserId);
        hold.Release();
        var response = await login;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TextOf(response)).ShouldContain(PasswordLogin.WrongMessage);
        var credential = await PasswordPeople.CredentialOfAsync(Host, person.UserId);
        (credential.State, credential.FailedCount).ShouldBe((PasswordState.Temporary, 1));
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();
        using var later = new AuthBrowser(Host);
        (await later.SignInAsync(person.LoginName, issued.GetProperty("password").GetString()!, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Property("Rule", "PWD-16")]
    public async Task a_choose_attempt_from_before_an_issue_gets_the_refused_page()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow, sameTenantAs: admin);
        using var adminBrowser = await SignedInAsync(admin);
        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        (await adminBrowser.IssueTemporaryAsync(admin, person.UserId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var choice = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(choosePage))), ("password", "a password picked after the issue")]);

        // Assert
        choice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TextOf(choice)).ShouldContain("This sign-in is not valid");
        browser.SessionCookie.ShouldBeNull();
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Temporary);
    }

    [Test]
    [Property("Rule", "PWD-16")]
    public async Task a_choose_attempt_from_before_any_change_of_the_credential_gets_the_refused_page()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow);
        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(person.LoginName, person.Password, NewAddress());
        using var stranger = new AuthBrowser(Host);
        (await stranger.SignInAsync(person.LoginName, "a wrong try of a stranger", NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Act
        var choice = await browser.PostAsync("/auth/password/choose", [("state", PasswordPeople.StateIn(await TextOf(choosePage))), ("password", "a password picked after the change")]);

        // Assert
        choice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Temporary);
    }

    private static async Task<AuthBrowser> SignedInAsync(PasswordPerson person)
    {
        var browser = new AuthBrowser(Host);
        (await browser.SignInAsync(person.LoginName, person.Password, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return browser;
    }

    [Test]
    [Property("Rule", "PWD-3")]
    public async Task a_setup_with_the_code_makes_the_owner_once_and_a_wrong_code_or_a_second_setup_changes_nothing()
    {
        // Arrange
        var setup = AuthHosts.Setup;
        var code = setup.Services.GetRequiredService<SetupCode>().Current!;
        using var browser = new AuthBrowser(setup);
        using var rival = new AuthBrowser(setup);
        var statusBefore = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();
        List<(string, string)> Fields(string state, string typedCode, string loginName) =>
            [("state", state), ("code", typedCode), ("name", "Ada Lovelace"), ("loginName", loginName), ("password", "a long first password of the owner")];
        var wrong = await browser.PostAsync("/auth/password/setup", Fields(await browser.FormStateAsync("/auth/password/setup"), "WRONGWRONGWRONGWRONG", "ada"));
        var tenantsAfterWrong = await setup.WithDatabaseAsync(db => db.Tenants.IgnoreQueryFilters().CountAsync());
        var firstState = await browser.FormStateAsync("/auth/password/setup");
        var rivalState = await rival.FormStateAsync("/auth/password/setup");

        // Act
        var both = await Task.WhenAll(
            browser.PostAsync("/auth/password/setup", Fields(firstState, code.ToLowerInvariant(), "ada")),
            rival.PostAsync("/auth/password/setup", Fields(rivalState, code, "bea")));
        var after = await new AuthBrowser(setup).GetAsync("/auth/password/setup");
        var status = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        statusBefore.GetProperty("setupRequired").GetBoolean().ShouldBeTrue();
        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TextOf(wrong)).ShouldContain(PasswordLogin.WrongCodeMessage);
        tenantsAfterWrong.ShouldBe(0);
        both.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.Redirect, HttpStatusCode.Conflict], ignoreOrder: true);
        after.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        setup.Services.GetRequiredService<SetupCode>().IsLive.ShouldBeFalse();
        status.GetProperty("setupRequired").GetBoolean().ShouldBeFalse();
        var owner = await setup.WithDatabaseAsync(db => db.Users.IgnoreQueryFilters().Include(u => u.UserPermissions).SingleAsync());
        owner.Role.Value.ShouldBe("Admin");
        owner.UserPermissions.Count.ShouldBe(Recall.Server.Domain.Users.UserRole.Admin().GetDefaultPermissions().Length);
        (await setup.WithDatabaseAsync(db => db.Set<PasswordCredential>().SingleAsync())).State.ShouldBe(PasswordState.Chosen);
    }

    [Test]
    [Property("Rule", "PWD-2")]
    [Property("Rule", "PWD-4")]
    public async Task an_install_with_a_provider_makes_no_setup_code_and_its_first_oidc_login_makes_the_owner()
    {
        // Arrange
        var host = AuthHosts.SetupWithProvider;
        using var browser = new AuthBrowser(host);
        var status = await (await browser.GetAsync("/auth/status")).Content.ReadFromJsonAsync<JsonElement>();
        var page = await browser.GetAsync("/auth/password/setup");
        var post = await browser.PostAsync("/auth/password/setup", [("state", "any"), ("code", "ANY"), ("name", "Ada"), ("loginName", "ada"), ("password", "a long first password of the owner")]);

        // Act
        await browser.LoginWebAsync($"owner-{Guid.NewGuid():N}");

        // Assert
        status.GetProperty("setupRequired").GetBoolean().ShouldBeFalse();
        host.Services.GetRequiredService<SetupCode>().IsLive.ShouldBeFalse();
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        post.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var owner = await host.WithDatabaseAsync(db => db.Users.IgnoreQueryFilters().SingleAsync());
        owner.Role.Value.ShouldBe("Admin");
        (await host.WithDatabaseAsync(db => db.Tenants.IgnoreQueryFilters().CountAsync())).ShouldBe(1);
    }
}
