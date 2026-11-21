namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class PasswordChangeTests
{
    private static AuthFlowFactory Host => AuthHosts.Password;

    private static readonly TokenGrant NoPermissions = new([], JournalAccess.None);

    private static string NewAddress() => $"2001:db8:{Random.Shared.Next(1, 0xffff):x}:{Random.Shared.Next(1, 0xffff):x}::1";

    private static Task<HttpResponseMessage> ChangeAsync(
        AuthBrowser browser, string current, string next, string? address = null, bool csrf = true, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AuthRoutes.PasswordChange)
        {
            Content = new FormUrlEncodedContent([KeyValuePair.Create("currentPassword", current), KeyValuePair.Create("newPassword", next)]),
        };
        if (csrf)
            request.Headers.Add("X-CSRF", "1");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (address is not null)
            request.Headers.Add(AuthFlowFactory.ConnectionAddressHeader, address);
        return browser.SendAsync(request);
    }

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> MeOf(AuthBrowser browser, bool csrf = true, string? authorization = null)
        => JsonDocument.Parse(await (await browser.GetAsync(AuthRoutes.Me, csrf, authorization)).Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> SessionIdOf(AuthBrowser browser)
        => JsonDocument.Parse(await (await browser.GetAsync(AuthRoutes.Me, csrf: true)).Content.ReadAsStringAsync()).RootElement.GetProperty("sid").GetString()!;

    private static Task<List<string>> LiveSessionsOf(string userId)
        => Host.WithDatabaseAsync(db => db.Set<AuthSession>().Where(s => s.UserId == userId && s.RevokedAt == null).Select(s => s.Id).ToListAsync());

    private static async Task<SignedIn> SignInAsync(PasswordPerson person, string? password = null)
    {
        var browser = new AuthBrowser(Host);
        (await browser.SignInAsync(person.LoginName, password ?? person.Password, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return new SignedIn(browser, await SessionIdOf(browser));
    }

    private sealed record SignedIn(AuthBrowser Browser, string SessionId) : IDisposable
    {
        public void Dispose() => Browser.Dispose();
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task a_change_with_the_current_password_ends_each_other_session_and_token_keeps_this_one_and_records_the_action()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        using var elsewhere = await SignInAsync(person);
        var token = await PatPeople.StartTokenAsync(Host, new PatPerson("", person.UserId, ""), NoPermissions);
        var next = $"a new journal passphrase {Guid.NewGuid():N}";

        // Act
        var response = await ChangeAsync(here.Browser, person.Password, next, NewAddress());

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LiveSessionsOf(person.UserId)).ShouldBe([here.SessionId]);
        (await PatPeople.RowAsync(Host, token.Token.Id)).RevokedReason.ShouldBe(RevokeReason.RevokeOthers);
        (await here.Browser.UserOfSessionAsync()).ShouldBe(person.UserId);
        using var oldPassword = new AuthBrowser(Host);
        (await oldPassword.SignInAsync(person.LoginName, person.Password, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var newPassword = await SignInAsync(person, next);
        (await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.RootId == person.UserId && e.Action == "password_changed"))).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task a_native_session_changes_the_password_with_its_bearer_token()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var browser = new AuthBrowser(Host);
        var app = NativeApp.New();
        var loginUrl = $"/auth/login?client=app&redirect_uri={Uri.EscapeDataString(app.Redirect)}&code_challenge={app.Challenge}&code_challenge_method=S256&state={app.ClientState}";
        var signedIn = await browser.PostLoginAsync(await browser.FormStateAsync(loginUrl), person.LoginName, person.Password, NewAddress());
        var code = QueryHelpers.ParseQuery(signedIn.Headers.Location!.Query)["code"].ToString();
        var tokens = await AuthBrowser.TokensOf(await browser.ExchangeAsync(code, app.Verifier, app.Redirect));

        // Act
        var response = await ChangeAsync(browser, person.Password, $"a new journal passphrase {Guid.NewGuid():N}", NewAddress(), csrf: false,
            authorization: $"Bearer {tokens.AccessToken}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LiveSessionsOf(person.UserId)).ShouldHaveSingleItem();
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task a_wrong_current_password_gets_403_at_its_field_counts_toward_the_stop_and_the_wait_and_changes_nothing()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        using var elsewhere = await SignInAsync(person);
        var address = NewAddress();
        var next = $"a new journal passphrase {Guid.NewGuid():N}";

        // Act
        var wrong = new List<HttpResponseMessage>();
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            wrong.Add(await ChangeAsync(here.Browser, "a wrong password for this person", next, address));
        var waiting = await ChangeAsync(here.Browser, person.Password, next, address);

        // Assert
        wrong.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Forbidden);
        var problem = await ProblemOf(wrong[0]);
        problem.GetProperty("detail").GetString().ShouldBe(PasswordLogin.WrongCurrentMessage);
        problem.GetProperty("errors").GetProperty("currentPassword")[0].GetString().ShouldBe(PasswordLogin.WrongCurrentMessage);
        waiting.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        waiting.Headers.RetryAfter.ShouldNotBeNull();
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).FailedCount.ShouldBe(PasswordBackoff.FreeTries);
        (await LiveSessionsOf(person.UserId)).Count.ShouldBe(2);
        using var stillOld = await SignInAsync(person);
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task the_100th_wrong_current_password_stops_the_credential_and_the_session_keeps_working()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        // The sign-in above resets the count, so the wrong tries go in after it.
        await Host.WithDatabaseAsync(async db =>
        {
            var credential = await db.Set<PasswordCredential>().SingleAsync(c => c.UserId == person.UserId);
            for (var i = 0; i < PasswordCredential.StopAt - 1; i++)
                credential.NoteFailure();
            return await db.SaveChangesAsync();
        });

        // Act
        var hundredth = await ChangeAsync(here.Browser, "a wrong password for this person", $"a new journal passphrase {Guid.NewGuid():N}", NewAddress());
        var right = await ChangeAsync(here.Browser, person.Password, $"a new journal passphrase {Guid.NewGuid():N}", NewAddress());

        // Assert
        hundredth.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemOf(hundredth)).GetProperty("detail").GetString().ShouldBe(PasswordLogin.StoppedMessage);
        right.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Stopped);
        (await here.Browser.UserOfSessionAsync()).ShouldBe(person.UserId);
    }

    [Test]
    [Property("Rule", "PWD-17")]
    [Arguments("short", PasswordPolicy.TooShortMessage)]
    [Arguments("1QAZ2WSX3EDC4RFV", PasswordPolicy.CommonMessage)]
    public async Task a_new_password_that_breaks_the_policy_gets_422_at_its_field_and_changes_nothing(string next, string message)
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        var before = (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash;

        // Act
        var response = await ChangeAsync(here.Browser, person.Password, next, NewAddress());

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ProblemOf(response)).GetProperty("errors").GetProperty("newPassword")[0].GetString().ShouldBe(message);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash.ShouldBe(before);
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task a_personal_access_token_gets_403_and_a_cookie_with_no_csrf_header_gets_401()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        var token = await PatPeople.StartTokenAsync(Host, new PatPerson("", person.UserId, ""), NoPermissions);
        using var tokenOnly = new AuthBrowser(Host);
        var before = (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash;
        var next = $"a new journal passphrase {Guid.NewGuid():N}";

        // Act
        var byToken = await ChangeAsync(tokenOnly, person.Password, next, csrf: false, authorization: $"Bearer {token.Value}");
        var noCsrf = await ChangeAsync(here.Browser, person.Password, next, csrf: false);

        // Assert
        byToken.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        noCsrf.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).PasswordHash.ShouldBe(before);
    }

    [Test]
    [Property("Rule", "PWD-17")]
    public async Task the_user_of_a_session_names_its_login_name_so_the_app_offers_the_change_only_to_a_person_with_a_password()
    {
        // Arrange
        var person = await PasswordPeople.AddAsync(Host);
        using var here = await SignInAsync(person);
        var linked = (await PatPeople.AddTenantAsync(AuthHosts.Closed)).Admin;
        using var oidc = new AuthBrowser(AuthHosts.Closed);
        await oidc.LoginWebAsync(linked.Subject);
        var token = await PatPeople.StartTokenAsync(Host, new PatPerson("", person.UserId, ""), NoPermissions);
        using var tokenOnly = new AuthBrowser(Host);
        // A temporary password signs in to nothing but the choose page, so the session of this person comes from the store.
        var temporary = await PasswordPeople.AddAsync(Host, issueTemporary: DateTimeOffset.UtcNow);
        var session = await Host.WithStoreAsync(store => store.StartWebAsync(temporary.UserId, PasswordLoginMode.ModeId, CancellationToken.None));
        using var withTemporary = new AuthBrowser(Host);
        withTemporary.SetCookie(withTemporary.SessionCookieName, session.CookieValue);

        // Act
        var withPassword = await MeOf(here.Browser);
        var withLink = await MeOf(oidc);
        var withToken = await MeOf(tokenOnly, csrf: false, authorization: $"Bearer {token.Value}");
        var withTemporaryPassword = await MeOf(withTemporary);

        // Assert
        withPassword.GetProperty("login_name").GetString().ShouldBe(person.LoginName);
        withLink.GetProperty("login_name").ValueKind.ShouldBe(JsonValueKind.Null);
        withToken.GetProperty("client_kind").GetString().ShouldBe("pat");
        withToken.GetProperty("login_name").ValueKind.ShouldBe(JsonValueKind.Null);
        withTemporaryPassword.GetProperty("sub").GetString().ShouldBe(temporary.UserId);
        withTemporaryPassword.GetProperty("login_name").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // A held check keeps one of the two hash slots, so only one such test runs at a time and the issue gets the other.
    [NotInParallel("held hash slot")]
    [Test]
    [Property("Rule", "PWD-17")]
    public async Task a_change_that_loaded_the_credential_before_an_issue_keeps_the_new_temporary_password()
    {
        // Arrange
        var current = $"the current passphrase that races {Guid.NewGuid():N}";
        var admin = await PasswordPeople.AddAsync(Host);
        var person = await PasswordPeople.AddAsync(Host, password: current, sameTenantAs: admin);
        using var adminSession = await SignInAsync(admin);
        using var here = await SignInAsync(person);
        var hold = Host.Hasher.HoldNextCheckOf(current);

        // Act
        var change = ChangeAsync(here.Browser, current, $"a new journal passphrase {Guid.NewGuid():N}", NewAddress());
        await hold.ReachedAsync();
        var issue = await adminSession.Browser.IssueTemporaryAsync(admin, person.UserId);
        hold.Release();
        var response = await change;

        // Assert
        issue.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(response)).GetProperty("detail").GetString().ShouldBe(PasswordLogin.NoPasswordToChangeMessage);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).State.ShouldBe(PasswordState.Temporary);
        (await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.RootId == person.UserId && e.Action == "password_changed"))).ShouldBe(0);
    }
}
