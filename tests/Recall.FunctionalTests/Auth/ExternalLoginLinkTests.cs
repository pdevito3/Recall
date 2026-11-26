namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

/// <summary>
/// A person links an identity at a provider to their own user, from their own web session, and then signs in with it. Each test adds a
/// tenant of its own on the <see cref="AuthHosts.Links"/> host, and each link names a subject that no other test uses.
/// </summary>
public sealed partial class ExternalLoginLinkTests
{
    private const string Mine = "/api/v1/me/external-logins";

    private static AuthFlowFactory Host => AuthHosts.Links;

    private static string OfUser(string userId) => $"/api/v1/users/{userId}/external-logins";

    private static string NewSubject() => $"link-{Guid.NewGuid():N}";

    private static async Task<(AuthBrowser Browser, PatPerson Admin, PatPerson Member)> SignedInAdminAsync()
    {
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(admin.Subject);
        return (browser, admin, member);
    }

    private static async Task<HttpResponseMessage> StartLinkAsync(AuthBrowser browser, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/auth/oidc/{AuthFlowFactory.ModeId}/link");
        if (csrf)
            request.Headers.Add("X-CSRF", "1");
        return await browser.SendAsync(request);
    }

    private static async Task<AuthorizeRequest> LinkAuthorizeAsync(AuthBrowser browser)
    {
        var started = await StartLinkAsync(browser);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        return AuthorizeRequest.Of(new Uri(body.RootElement.GetProperty("url").GetString()!));
    }

    private static string? LinkResultOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.ShouldStartWith(OidcEndpoints.SettingsPath + "?");
        return QueryHelpers.ParseQuery(location[OidcEndpoints.SettingsPath.Length..])[OidcEndpoints.LinkResultQuery].ToString();
    }

    private static Task<List<ExternalLogin>> LinksOfAsync(string userId)
        => Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().AsNoTracking().Where(l => l.UserId == userId).ToListAsync());

    private static Task<int> SessionCountAsync(string userId)
        => Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == userId));

    private static async Task<HttpResponseMessage> SendApiAsync(AuthBrowser browser, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }

    private static Task<int> AgeSessionAsync(string userId, TimeSpan age)
        => Host.WithDatabaseAsync(db => db.Set<AuthSession>()
            .Where(s => s.UserId == userId && s.ClientKind == SessionKind.Web)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.CreatedAt, DateTimeOffset.UtcNow - age)));

    [Test]
    [Property("Rule", "IDP-9")]
    [Property("Rule", "IDP-11")]
    public async Task a_link_from_a_fresh_web_session_adds_the_identity_to_the_person_and_starts_no_session()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var cookie = browser.SessionCookie;
        var sessions = await SessionCountAsync(admin.UserId);
        var subject = NewSubject();

        // Act
        var authorize = await LinkAuthorizeAsync(browser);
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject) { Email = "ada@example.test" }));

        // Assert
        authorize.Prompt.ShouldBe(AuthPages.PromptLogin);
        LinkResultOf(callback).ShouldBe(OidcEndpoints.Linked);
        var link = (await LinksOfAsync(admin.UserId)).Single(l => l.Subject == subject);
        link.Issuer.ShouldBe(StubOidcProvider.Issuer);
        link.DisplayLabel.ShouldBe("ada@example.test");
        browser.SessionCookie.ShouldBe(cookie);
        (await SessionCountAsync(admin.UserId)).ShouldBe(sessions);
        using var person = new AuthBrowser(Host);
        await person.LoginWebAsync(subject);
        (await person.UserOfSessionAsync()).ShouldBe(await browser.UserOfSessionAsync());
    }

    [Test]
    [Property("Rule", "IDP-11")]
    public async Task a_link_with_no_email_shows_the_name_of_the_provider()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();

        // Act
        var authorize = await LinkAuthorizeAsync(browser);
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject) { Name = "Ada at work" }));
        var listed = await SendApiAsync(browser, HttpMethod.Get, Mine);

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.Linked);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var item = body.RootElement.EnumerateArray().Single(e => e.GetProperty("subject").GetString() == subject);
        item.GetProperty("displayLabel").GetString().ShouldBe("Ada at work");
        item.GetProperty("providerId").GetString().ShouldBe(AuthFlowFactory.ModeId);
        item.GetProperty("providerName").GetString().ShouldBe("Stub");
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task the_session_cookie_that_a_provider_redirect_holds_back_comes_on_a_second_load_from_recall()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var cookie = browser.SessionCookie!;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        var code = Host.Provider.Approve(authorize, new StubLogin(subject));

        // Act
        browser.DropCookie(browser.SessionCookieName);
        var first = await browser.CallbackAsync(authorize.State, code);
        browser.SetCookie(browser.SessionCookieName, cookie);
        var again = WebUtility.HtmlDecode(ContinueLink().Match(await first.Content.ReadAsStringAsync()).Groups["url"].Value);
        var second = await browser.GetAsync(again);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.ShouldStartWith(AuthRoutes.Callback + "?");
        LinkResultOf(second).ShouldBe(OidcEndpoints.Linked);
        (await LinksOfAsync(admin.UserId)).ShouldContain(l => l.Subject == subject);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_replay_of_the_second_load_after_a_link_adds_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var cookie = browser.SessionCookie!;
        var authorize = await LinkAuthorizeAsync(browser);
        var code = Host.Provider.Approve(authorize, new StubLogin(NewSubject()));
        browser.DropCookie(browser.SessionCookieName);
        var again = await SecondLoadOfAsync(browser, authorize.State, code);
        browser.SetCookie(browser.SessionCookieName, cookie);
        LinkResultOf(await browser.GetAsync(again)).ShouldBe(OidcEndpoints.Linked);
        var links = await LinksOfAsync(admin.UserId);

        // Act
        var replay = await browser.GetAsync(again);

        // Assert
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await replay.Content.ReadAsStringAsync()).ShouldContain("This sign-in is not valid");
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(links.Count);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_second_load_that_still_holds_no_session_cookie_adds_nothing_and_asks_no_third()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        var code = Host.Provider.Approve(authorize, new StubLogin(subject));
        browser.DropCookie(browser.SessionCookieName);
        var again = await SecondLoadOfAsync(browser, authorize.State, code);

        // Act
        var second = await browser.GetAsync(again);

        // Assert
        LinkResultOf(second).ShouldBe(OidcEndpoints.LinkRefused);
        (await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.Subject == subject))).ShouldBe(0);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_second_load_with_no_binding_cookie_adds_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var cookie = browser.SessionCookie!;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        var code = Host.Provider.Approve(authorize, new StubLogin(subject));
        browser.DropCookie(browser.SessionCookieName);
        var again = await SecondLoadOfAsync(browser, authorize.State, code);
        browser.SetCookie(browser.SessionCookieName, cookie);
        foreach (var name in browser.Cookies.Keys.Where(name => name.Contains("recall_login_", StringComparison.Ordinal)).ToList())
            browser.DropCookie(name);

        // Act
        var second = await browser.GetAsync(again);

        // Assert
        LinkResultOf(second).ShouldBe(OidcEndpoints.LinkRefused);
        (await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.Subject == subject))).ShouldBe(0);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_provider_that_does_not_accept_prompt_login_gets_no_link_and_its_login_still_starts()
    {
        // Arrange
        var host = AuthHosts.NoPromptLogin;
        var (admin, _) = await PatPeople.AddTenantAsync(host);
        using var browser = new AuthBrowser(host);
        await browser.LoginWebAsync(admin.Subject);
        var attempts = await host.WithDatabaseAsync(db => db.Set<LoginAttempt>().CountAsync(a => a.LinkSessionId != null));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/auth/oidc/{AuthFlowFactory.ModeId}/link");
        request.Headers.Add("X-CSRF", "1");

        // Act
        var started = await browser.SendAsync(request);
        using var stranger = new AuthBrowser(host);
        var login = await stranger.StartWebAsync();

        // Assert
        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(started)).ShouldBe(OidcEndpoints.NoPromptLoginMessage("Stub"));
        (await host.WithDatabaseAsync(db => db.Set<LoginAttempt>().CountAsync(a => a.LinkSessionId != null))).ShouldBe(attempts);
        login.State.ShouldNotBeNullOrEmpty();
        login.Prompt.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_link_that_comes_back_in_another_browser_adds_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        using var stranger = new AuthBrowser(Host);
        await stranger.LoginWebAsync((await PatPeople.AddTenantAsync(Host)).Admin.Subject);

        // Act
        var callback = await stranger.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject)));

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.LinkRefused);
        (await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.Subject == subject))).ShouldBe(0);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_link_whose_browser_now_holds_another_session_adds_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        var other = (await PatPeople.AddTenantAsync(Host)).Admin;
        await browser.LoginWebAsync(other.Subject);

        // Act
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject)));

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.LinkRefused);
        (await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.Subject == subject))).ShouldBe(0);
        (await LinksOfAsync(other.UserId)).Count.ShouldBe(1);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_link_after_its_session_ended_adds_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var cookie = browser.SessionCookie!;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        (await browser.PostFormAsync(AuthRoutes.Logout, [], csrf: true)).IsSuccessStatusCode.ShouldBeTrue();
        // The browser that ended the session still holds its old cookie.
        browser.SetCookie(browser.SessionCookieName, cookie);

        // Act
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject)));

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.LinkRefused);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-9")]
    public async Task a_link_needs_a_web_session_and_the_csrf_header()
    {
        // Arrange
        var (browser, _, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        using var anonymous = new AuthBrowser(Host);

        // Act
        var noHeader = await StartLinkAsync(browser, csrf: false);
        var noSession = await StartLinkAsync(anonymous);

        // Assert
        noHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Property("Rule", "IDP-10")]
    public async Task a_session_older_than_15_minutes_gets_403_and_starts_no_link()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        await AgeSessionAsync(admin.UserId, TimeSpan.FromMinutes(16));
        var attempts = await Host.WithDatabaseAsync(db => db.Set<LoginAttempt>().CountAsync(a => db.Set<AuthSession>().Any(s => s.Id == a.LinkSessionId && s.UserId == admin.UserId)));

        // Act
        var old = await StartLinkAsync(browser);
        await AgeSessionAsync(admin.UserId, TimeSpan.FromMinutes(14));
        var fresh = await StartLinkAsync(browser);

        // Assert
        old.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(old)).ShouldBe(OidcEndpoints.SignInAgainToLinkMessage);
        using (var problem = JsonDocument.Parse(await old.Content.ReadAsStringAsync()))
            problem.RootElement.GetProperty("type").GetString().ShouldBe(OidcEndpoints.SignInAgainToLinkType);
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.WithDatabaseAsync(db => db.Set<LoginAttempt>().CountAsync(a => db.Set<AuthSession>().Any(s => s.Id == a.LinkSessionId && s.UserId == admin.UserId)))).ShouldBe(attempts + 1);
    }

    [Test]
    [Property("Rule", "IDP-11")]
    public async Task an_identity_linked_to_another_person_gets_409_and_nothing_changes()
    {
        // Arrange
        var (browser, admin, member) = await SignedInAdminAsync();
        using var signedIn = browser;
        var authorize = await LinkAuthorizeAsync(browser);

        // Act
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(member.Subject)));

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.LinkedToAnother);
        (await LinksOfAsync(admin.UserId)).ShouldHaveSingleItem().Subject.ShouldBe(admin.Subject);
        (await LinksOfAsync(member.UserId)).ShouldHaveSingleItem().Subject.ShouldBe(member.Subject);
    }

    [Test]
    [Property("Rule", "IDP-11")]
    public async Task an_identity_already_linked_to_the_person_succeeds_and_changes_nothing()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var before = (await LinksOfAsync(admin.UserId)).ShouldHaveSingleItem();
        var authorize = await LinkAuthorizeAsync(browser);

        // Act
        var callback = await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(admin.Subject) { Email = "new@example.test" }));

        // Assert
        LinkResultOf(callback).ShouldBe(OidcEndpoints.Linked);
        (await LinksOfAsync(admin.UserId)).ShouldHaveSingleItem().ShouldBe(before);
        (await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.RootId == admin.UserId && e.Action == "external_login_linked"))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-11")]
    public async Task a_matching_email_links_no_one()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var email = await Host.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == admin.UserId);
            user.UpdateFromIdp(user.FirstName, user.LastName, $"{Guid.NewGuid():N}@example.test", user.Username);
            await db.SaveChangesAsync();
            return user.Email!.Value;
        });
        using var stranger = new AuthBrowser(Host);
        var authorize = await stranger.StartWebAsync();

        // Act
        var callback = await stranger.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(NewSubject()) { Email = email }));

        // Assert
        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.SessionCookie.ShouldBeNull();
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    [Property("Rule", "IDP-18")]
    public async Task a_person_lists_and_removes_their_own_link_and_the_audit_log_names_the_user()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        LinkResultOf(await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject) { Email = "ada@example.test" })))
            .ShouldBe(OidcEndpoints.Linked);

        // Act
        var before = await SendApiAsync(browser, HttpMethod.Get, Mine);
        var removed = await SendApiAsync(browser, HttpMethod.Delete, Mine, new { issuer = StubOidcProvider.Issuer, subject });
        var again = await SendApiAsync(browser, HttpMethod.Delete, Mine, new { issuer = StubOidcProvider.Issuer, subject });
        var after = await SendApiAsync(browser, HttpMethod.Get, Mine);

        // Assert
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
            body.RootElement.GetArrayLength().ShouldBe(2);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
            body.RootElement.EnumerateArray().Select(e => e.GetProperty("subject").GetString()).ShouldBe([admin.Subject]);
        var entries = await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.RootId == admin.UserId && e.Action != null && e.Action.StartsWith("external_login_"))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync());
        entries.Select(e => e.Action).ShouldBe(["external_login_linked", "external_login_removed"]);
        entries.ShouldAllBe(e => e.EntityId == admin.UserId && e.Data.Contains(StubOidcProvider.Issuer));
        entries.ShouldAllBe(e => e.ActorUserId == admin.UserId && e.ActorClient != null);
    }

    [Test]
    [Property("Rule", "IDP-18")]
    public async Task a_reader_with_no_sensitive_permission_sees_no_subject_of_a_link()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        LinkResultOf(await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject)))).ShouldBe(OidcEndpoints.Linked);
        var reader = await PatPeople.AddPersonAsync(Host, admin.TenantId, [], Permission.ReadAuditLog(), Permission.ReadUsers());
        using var readerBrowser = new AuthBrowser(Host);
        await readerBrowser.LoginWebAsync(reader.Subject);

        // Act
        var page = await SendApiAsync(readerBrowser, HttpMethod.Get, $"/api/v1/audit-entries?entityId={admin.UserId}");
        var text = await page.Content.ReadAsStringAsync();

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        text.ShouldContain("external_login_linked");
        text.ShouldNotContain(subject);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task an_admin_with_manage_user_access_lists_and_removes_the_link_of_a_member()
    {
        // Arrange
        var (browser, _, member) = await SignedInAdminAsync();
        using var signedIn = browser;

        // Act
        var listed = await SendApiAsync(browser, HttpMethod.Get, OfUser(member.UserId));
        var removed = await SendApiAsync(browser, HttpMethod.Delete, OfUser(member.UserId), new { issuer = StubOidcProvider.Issuer, subject = member.Subject });

        // Assert
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()))
            body.RootElement.EnumerateArray().Select(e => e.GetProperty("subject").GetString()).ShouldBe([member.Subject]);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LinksOfAsync(member.UserId)).ShouldBeEmpty();
        (await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.RootId == member.UserId && e.Action == "external_login_removed"))).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task a_removal_from_your_own_links_that_names_the_link_of_another_person_leaves_it()
    {
        // Arrange
        var (browser, admin, member) = await SignedInAdminAsync();
        using var signedIn = browser;

        // Act
        var removed = await SendApiAsync(browser, HttpMethod.Delete, Mine, new { issuer = StubOidcProvider.Issuer, subject = member.Subject });

        // Assert
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LinksOfAsync(member.UserId)).ShouldHaveSingleItem().Subject.ShouldBe(member.Subject);
        (await LinksOfAsync(admin.UserId)).ShouldHaveSingleItem().Subject.ShouldBe(admin.Subject);
        (await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.RootId == member.UserId && e.Action == "external_login_removed"))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task a_caller_who_lacks_a_permission_of_the_user_gets_403_and_nothing_changes()
    {
        // Arrange
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var manager = await PatPeople.AddPersonAsync(Host, admin.TenantId, [], Permission.ManageUserAccess(), Permission.ReadUsers());
        using var memberBrowser = new AuthBrowser(Host);
        await memberBrowser.LoginWebAsync(member.Subject);
        using var managerBrowser = new AuthBrowser(Host);
        await managerBrowser.LoginWebAsync(manager.Subject);
        var body = new { issuer = StubOidcProvider.Issuer, subject = admin.Subject };

        // Act
        var memberList = await SendApiAsync(memberBrowser, HttpMethod.Get, OfUser(admin.UserId));
        var memberRemove = await SendApiAsync(memberBrowser, HttpMethod.Delete, OfUser(admin.UserId), body);
        var managerList = await SendApiAsync(managerBrowser, HttpMethod.Get, OfUser(admin.UserId));
        var managerRemove = await SendApiAsync(managerBrowser, HttpMethod.Delete, OfUser(admin.UserId), body);

        // Assert
        foreach (var response in new[] { memberList, memberRemove, managerList, managerRemove })
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task a_removal_of_the_last_way_in_of_the_last_admin_gets_409_and_nothing_changes()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;

        // Act
        var removed = await SendApiAsync(browser, HttpMethod.Delete, Mine, new { issuer = StubOidcProvider.Issuer, subject = admin.Subject });

        // Assert
        removed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(removed)).ShouldBe(LastAdmin.RefusedMessage);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task a_removal_of_one_of_two_links_of_the_last_admin_succeeds()
    {
        // Arrange
        var (browser, admin, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = NewSubject();
        var authorize = await LinkAuthorizeAsync(browser);
        LinkResultOf(await browser.CallbackAsync(authorize.State, Host.Provider.Approve(authorize, new StubLogin(subject)))).ShouldBe(OidcEndpoints.Linked);

        // Act
        var removed = await SendApiAsync(browser, HttpMethod.Delete, Mine, new { issuer = StubOidcProvider.Issuer, subject = admin.Subject });

        // Assert
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LinksOfAsync(admin.UserId)).ShouldHaveSingleItem().Subject.ShouldBe(subject);
    }

    [Test]
    [Property("Rule", "IDP-12")]
    public async Task a_native_session_lists_its_links_and_cannot_start_one()
    {
        // Arrange
        var (admin, _) = await PatPeople.AddTenantAsync(Host);
        using var app = new AuthBrowser(Host);
        var tokens = await app.LoginNativeAsync(admin.Subject);
        using var client = PatPeople.ClientWith(Host, tokens.AccessToken);

        // Act
        var listed = await client.GetAsync(Mine);
        var start = await client.PostAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/link", null);

        // Assert
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        start.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(start)).ShouldBe(OidcEndpoints.WebSessionOnlyMessage);
    }

    // IDP-19 also covers the login-settings routes of a later PR, so these tests do not name it yet.
    [Test]
    public async Task a_personal_access_token_and_a_provider_token_get_403_on_each_link_route()
    {
        // Arrange
        var (admin, _) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, admin, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var pat = PatPeople.ClientWith(Host, token.Value);
        using var provider = Host.NewClient();
        provider.DefaultRequestHeaders.Authorization = AuthBrowser.Bearer(
            Host.Provider.SignJwt(new Dictionary<string, object> { ["sub"] = admin.Subject, ["client_id"] = "machine" }));
        var body = new { issuer = StubOidcProvider.Issuer, subject = admin.Subject };

        // Act
        var responses = new List<HttpResponseMessage>();
        foreach (var client in new[] { pat, provider })
        {
            responses.Add(await client.GetAsync(Mine));
            responses.Add(await client.GetAsync(OfUser(admin.UserId)));
            responses.Add(await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, Mine) { Content = JsonContent.Create(body) }));
            responses.Add(await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, OfUser(admin.UserId)) { Content = JsonContent.Create(body) }));
            responses.Add(await client.PostAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/link", null));
        }

        // Assert
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Forbidden);
        (await LinksOfAsync(admin.UserId)).Count.ShouldBe(1);
    }

    /// <summary>The callback without the session cookie, as the redirect of a provider comes, and the URL of the page that loads it again.</summary>
    private static async Task<string> SecondLoadOfAsync(AuthBrowser browser, string state, string code)
    {
        var first = await browser.CallbackAsync(state, code);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(ContinueLink().Match(await first.Content.ReadAsStringAsync()).Groups["url"].Value);
    }

    [GeneratedRegex("<a class=\"button\" href=\"(?<url>[^\"]+)\">Continue</a>")]
    private static partial Regex ContinueLink();
}
