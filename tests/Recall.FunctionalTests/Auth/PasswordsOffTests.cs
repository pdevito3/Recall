namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.LoginProviders;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

/// <summary>
/// An Admin turns password login off for the install. The setting is install-wide, so the tests of this class run one at a time on the
/// <see cref="AuthHosts.PasswordsOff"/> host, and each starts with password login On. Each tenant that a test leaves keeps an Admin with a
/// link to the provider of the host, so a later test can turn passwords off.
/// </summary>
[NotInParallel(nameof(PasswordsOffTests))]
public class PasswordsOffTests
{
    private const string Settings = "/api/v1/login-settings";
    private const string Providers = "/api/v1/login-providers";
    private const string RowId = "row";

    private static AuthFlowFactory Host => AuthHosts.PasswordsOff;

    private static LoginModes Modes => Host.Services.GetRequiredService<LoginModes>();

    /// <summary>Password login On and no row of the app, as each test expects at its start.</summary>
    private static async Task ResetAsync(AuthFlowFactory host)
    {
        await host.WithDatabaseAsync(async db =>
        {
            var settings = await db.LoginSettings.SingleAsync();
            if (settings.PasswordLogin == PasswordLoginState.Off)
                settings.TurnPasswordsOn(settings.Version, null!);
            db.LoginProviders.RemoveRange(await db.LoginProviders.ToListAsync());
            return await db.SaveChangesAsync();
        });
        await host.Services.GetRequiredService<LoginModes>().ReloadAsync(CancellationToken.None);
    }

    private static async Task<(AuthBrowser Browser, PatPerson Admin)> SignedInAdminAsync()
    {
        var (admin, _) = await PatPeople.AddTenantAsync(Host);
        var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(admin.Subject);
        return (browser, admin);
    }

    private static async Task<HttpResponseMessage> SendAsync(AuthBrowser browser, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
    }

    private static async Task<uint> VersionAsync(AuthBrowser browser)
    {
        using var settings = JsonDocument.Parse(await (await SendAsync(browser, HttpMethod.Get, Settings)).Content.ReadAsStringAsync());
        return settings.RootElement.GetProperty("version").GetUInt32();
    }

    private static async Task<HttpResponseMessage> TurnAsync(AuthBrowser browser, PasswordLoginState state)
        => await SendAsync(browser, HttpMethod.Put, Settings, new { passwordLogin = state.ToString(), version = await VersionAsync(browser) });

    private static async Task TurnOffAsync(AuthBrowser browser)
        => (await TurnAsync(browser, PasswordLoginState.Off)).StatusCode.ShouldBe(HttpStatusCode.OK);

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }

    private static Task<int> LinkAsync(string userId, string issuer = StubOidcProvider.Issuer)
        => Host.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<ExternalLogin>().Add(new ExternalLogin { Issuer = issuer, Subject = $"off-{Guid.NewGuid():N}", UserId = userId, CreatedAt = now, LastLoginAt = now });
            return await db.SaveChangesAsync();
        });

    /// <summary>A person of <paramref name="tenantId"/> with a password and no link.</summary>
    private static async Task<PasswordPerson> AddPasswordUserAsync(
        AuthFlowFactory host, string tenantId, UserRole role, DateTimeOffset? issueTemporary = null, int wrongTries = 0)
    {
        var loginName = $"off-{Guid.NewGuid():N}";
        var password = $"a long journal passphrase {Guid.NewGuid():N}";
        var userId = await host.WithDatabaseAsync(async db =>
        {
            var user = User.CreateFromLogin(new NewPerson("Grace", "Hopper", null, "grace"), tenantId, role);
            LoginName.TryOf(loginName, out var name).ShouldBeTrue();
            var hash = PasswordPeople.HashOf(password);
            var credential = issueTemporary is { } issuedAt
                ? PasswordCredential.IssueTemporary(user.Id, name!, hash, issuedAt)
                : PasswordCredential.Choose(user.Id, name!, hash, DateTimeOffset.UtcNow);
            for (var i = 0; i < wrongTries; i++)
                credential.NoteFailure();
            db.Users.Add(user);
            db.Set<PasswordCredential>().Add(credential);
            await db.SaveChangesAsync();
            return user.Id;
        });
        return new PasswordPerson(userId, loginName, password);
    }

    private static Task<string> AddTenantAsync(AuthFlowFactory host) => host.WithDatabaseAsync(async db =>
    {
        var tenant = Tenant.Create(new TenantForCreation { Name = $"Off {Guid.NewGuid():N}" });
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    });

    [Test]
    [Property("Rule", "IDP-13")]
    public async Task a_turn_off_and_on_changes_the_setting_and_records_each_action()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;

        // Act
        var off = await TurnAsync(browser, PasswordLoginState.Off);
        var offModes = Modes.PasswordsAreOn;
        var on = await TurnAsync(browser, PasswordLoginState.On);

        // Assert
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await off.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("passwordLogin").GetString().ShouldBe("Off");
        offModes.ShouldBeFalse();
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        Modes.PasswordsAreOn.ShouldBeTrue();
        var actions = await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == admin.TenantId && e.Action != null && e.Action.StartsWith("password_login_"))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.Action, e.ActorUserId })
            .ToListAsync());
        actions.Select(a => a.Action).ShouldBe(["password_login_turned_off", "password_login_turned_on"]);
        actions.ShouldAllBe(a => a.ActorUserId == admin.UserId);
    }

    [Test]
    [Property("Rule", "IDP-13")]
    public async Task a_turn_off_with_a_stale_version_gets_a_conflict_and_changes_nothing()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var version = await VersionAsync(browser);

        // Act
        var stale = await SendAsync(browser, HttpMethod.Put, Settings, new { passwordLogin = "Off", version = version + 1 });

        // Assert
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Modes.PasswordsAreOn.ShouldBeTrue();
        (await VersionAsync(browser)).ShouldBe(version);
    }

    [Test]
    [Property("Rule", "IDP-13")]
    public async Task a_turn_off_while_a_tenant_has_no_admin_with_a_link_to_a_usable_provider_gets_a_conflict()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var tenantId = await AddTenantAsync(Host);
        var stranded = await AddPasswordUserAsync(Host, tenantId, UserRole.Admin());
        await LinkAsync(stranded.UserId, "https://gone.example");

        // Act
        var refused = await TurnAsync(browser, PasswordLoginState.Off);
        var onAfterRefusal = Modes.PasswordsAreOn;
        await LinkAsync(stranded.UserId);
        var allowed = await TurnAsync(browser, PasswordLoginState.Off);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(refused)).ShouldBe(LoginSettings.KeepsNoLinkedAdminMessage);
        onAfterRefusal.ShouldBeTrue();
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        Modes.PasswordsAreOn.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IDP-14")]
    public async Task while_passwords_are_off_an_admin_with_only_a_password_does_not_count_as_another_admin()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var other = await AddPasswordUserAsync(Host, admin.TenantId, UserRole.Admin());
        await TurnOffAsync(browser);

        // Act
        var refused = await SendAsync(browser, HttpMethod.Put, $"/api/v1/users/{admin.UserId}/role", new { role = "User" });
        (await TurnAsync(browser, PasswordLoginState.On)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var allowed = await SendAsync(browser, HttpMethod.Put, $"/api/v1/users/{admin.UserId}/role", new { role = "User" });

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(refused)).ShouldBe(LastAdmin.RefusedMessage);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        await LinkAsync(other.UserId);
    }

    [Test]
    [Property("Rule", "IDP-14")]
    public async Task while_passwords_are_off_a_delete_of_the_provider_of_the_last_linked_admin_gets_a_conflict()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var row = new { id = RowId, displayName = "Row", issuer = AuthFlowFactory.RowIssuer, clientId = StubOidcProvider.ClientId, clientSecret = Host.RowProvider.ClientSecret, allowHttp = false };
        (await SendAsync(browser, HttpMethod.Post, Providers, row)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tenantId = await AddTenantAsync(Host);
        var rowAdmin = await AddPasswordUserAsync(Host, tenantId, UserRole.Admin());
        await LinkAsync(rowAdmin.UserId, AuthFlowFactory.RowIssuer);
        await TurnOffAsync(browser);

        // Act
        var refused = await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}");
        (await TurnAsync(browser, PasswordLoginState.On)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var allowed = await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}");

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(refused)).ShouldBe(LastAdmin.RefusedMessage);
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await LinkAsync(rowAdmin.UserId);
    }

    [Test]
    [Property("Rule", "IDP-13")]
    [Property("Rule", "IDP-14")]
    public async Task a_removal_of_the_last_link_of_an_admin_during_a_turn_off_waits_for_it_and_then_gets_a_conflict()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var tenantId = await AddTenantAsync(Host);
        var last = await AddPasswordUserAsync(Host, tenantId, UserRole.Admin());
        await LinkAsync(last.UserId);
        var link = await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().AsNoTracking().SingleAsync(l => l.UserId == last.UserId));
        using var lastBrowser = new AuthBrowser(Host);
        (await lastBrowser.SignInAsync(last.LoginName, last.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var version = await VersionAsync(browser);
        var pause = PauseAtQuery.After("users.last_admin.linked_admins");

        // Act
        HttpResponseMessage turned;
        HttpResponseMessage removed;
        Host.Pause = pause;
        try
        {
            var turn = SendAsync(browser, HttpMethod.Put, Settings, new { passwordLogin = "Off", version });
            await pause.ReachedAsync();
            var removal = SendAsync(lastBrowser, HttpMethod.Delete, "/api/v1/me/external-logins", new { issuer = link.Issuer, subject = link.Subject });
            await pause.WaitUntilBlockedAsync(removal, () => RootLock.Tenant.HasWaiter(tenantId));
            pause.Release();
            turned = await turn;
            removed = await removal;
        }
        finally
        {
            pause.Release();
            Host.Pause = null;
        }

        // Assert
        turned.StatusCode.ShouldBe(HttpStatusCode.OK);
        removed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(removed)).ShouldBe(LastAdmin.RefusedMessage);
        Modes.PasswordsAreOn.ShouldBeFalse();
        (await Host.WithDatabaseAsync(db => db.Set<ExternalLogin>().AnyAsync(l => l.UserId == last.UserId))).ShouldBeTrue();
        var tenants = await Host.WithDatabaseAsync(db => LastAdmin.LoadAdminLinksAsync(db, CancellationToken.None));
        LastAdmin.EachTenantHasALinkedAdmin(tenants, Modes.UsableIssuers).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "IDP-15")]
    public async Task a_sign_in_whose_password_was_checked_before_a_turn_off_saved_starts_no_session()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var person = await AddPasswordUserAsync(Host, admin.TenantId, UserRole.User());
        using var personBrowser = new AuthBrowser(Host);
        var state = await personBrowser.FormStateAsync();
        var pause = PauseAtQuery.After("auth.password.find_credential");

        // Act
        HttpResponseMessage login;
        Host.Pause = pause;
        try
        {
            var signIn = personBrowser.PostLoginAsync(state, person.LoginName, person.Password);
            await pause.ReachedAsync();
            await TurnOffAsync(browser);
            pause.Release();
            login = await signIn;
        }
        finally
        {
            pause.Release();
            Host.Pause = null;
        }

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await login.Content.ReadAsStringAsync()).ShouldContain(PasswordLogin.WrongMessage);
        personBrowser.SessionCookie.ShouldBeNull();
        (await Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == person.UserId))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-19")]
    public async Task a_personal_access_token_and_a_provider_token_get_403_on_the_login_settings()
    {
        // Arrange
        var host = AuthHosts.FirstPartyClient;
        var (admin, _) = await PatPeople.AddTenantAsync(host);
        var token = await PatPeople.StartTokenAsync(host, admin, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var pat = PatPeople.ClientWith(host, token.Value);
        using var provider = PatPeople.ClientWith(host, host.Provider.SignJwt(new Dictionary<string, object> { ["sub"] = admin.Subject, ["client_id"] = AuthHosts.FirstPartyClientId }));
        var body = new { passwordLogin = "Off", version = 1 };

        // Act
        HttpResponseMessage[] responses =
        [
            await pat.GetAsync(Settings),
            await pat.PutAsJsonAsync(Settings, body),
            await provider.GetAsync(Settings),
            await provider.PutAsJsonAsync(Settings, body),
        ];

        // Assert
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Forbidden);
        (await host.WithDatabaseAsync(db => db.LoginSettings.SingleAsync())).PasswordLogin.ShouldBe(PasswordLoginState.On);
    }

    private static async Task<HttpResponseMessage> StatusAsync(AuthFlowFactory host)
    {
        using var client = host.NewClient();
        return await client.GetAsync(AuthRoutes.Status);
    }

    private static async Task<IReadOnlyList<string>> StatusModeIdsAsync(AuthFlowFactory host)
    {
        using var status = JsonDocument.Parse(await (await StatusAsync(host)).Content.ReadAsStringAsync());
        return status.RootElement.GetProperty("modes").EnumerateArray().Select(mode => mode.GetProperty("id").GetString()!).ToList();
    }

    [Test]
    [Property("Rule", "IDP-15")]
    public async Task while_passwords_are_off_no_password_signs_in_and_a_session_and_a_token_from_a_password_keep_working()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var person = await AddPasswordUserAsync(Host, admin.TenantId, UserRole.User());
        var stopped = await AddPasswordUserAsync(Host, admin.TenantId, UserRole.User(), wrongTries: PasswordCredential.StopAt);
        using var personBrowser = new AuthBrowser(Host);
        (await personBrowser.SignInAsync(person.LoginName, person.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var token = await PatPeople.StartTokenAsync(Host, new PatPerson(person.LoginName, person.UserId, admin.TenantId), new TokenGrant([], JournalAccess.None));
        using var tokenClient = PatPeople.ClientWith(Host, token.Value);
        var modesWhileOn = await StatusModeIdsAsync(Host);
        await TurnOffAsync(browser);

        // Act
        var modesWhileOff = await StatusModeIdsAsync(Host);
        using var visitor = new AuthBrowser(Host);
        var page = await visitor.GetAsync($"{AuthRoutes.Login}?signedOut=1");
        using var other = new AuthBrowser(Host);
        var chosen = await other.PostLoginAsync(await other.FormStateAsync($"{AuthRoutes.Login}?{AuthPages.TemporaryPasswordQuery}=1"), person.LoginName, person.Password);
        var stoppedLogin = await other.PostLoginAsync(await other.FormStateAsync($"{AuthRoutes.Login}?{AuthPages.TemporaryPasswordQuery}=1"), stopped.LoginName, stopped.Password);
        var me = await personBrowser.GetAsync("/auth/me", csrf: true);
        var tokenMe = await tokenClient.GetAsync("/auth/me");
        var change = await personBrowser.PostFormAsync(AuthRoutes.PasswordChange, [("currentPassword", person.Password), ("newPassword", "another long passphrase of mine")], csrf: true);
        var choose = await other.PostAsync(AuthRoutes.PasswordChoose, [("state", "any"), ("password", "another long passphrase of mine")]);

        // Assert
        modesWhileOn.ShouldContain(PasswordLoginMode.ModeId);
        modesWhileOff.ShouldBe([AuthFlowFactory.ModeId]);
        (await page.Content.ReadAsStringAsync()).ShouldNotContain($"action=\"{AuthRoutes.PasswordLogin}\"");
        chosen.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await chosen.Content.ReadAsStringAsync()).ShouldContain(PasswordLogin.WrongMessage);
        stoppedLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var stoppedText = await stoppedLogin.Content.ReadAsStringAsync();
        stoppedText.ShouldContain(PasswordLogin.WrongMessage);
        stoppedText.ShouldNotContain(PasswordLogin.StoppedMessage);
        other.SessionCookie.ShouldBeNull();
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("login_name").ValueKind.ShouldBe(JsonValueKind.Null);
        tokenMe.StatusCode.ShouldBe(HttpStatusCode.OK);
        change.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        choose.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PasswordPeople.CredentialOfAsync(Host, person.UserId)).FailedCount.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-15")]
    [Property("Rule", "IDP-20")]
    public async Task with_passwords_off_and_no_usable_provider_the_status_has_no_login_method_and_the_page_points_to_the_claim_file()
    {
        // Arrange
        var host = AuthHosts.NoLoginMethod;
        await ResetAsync(host);
        var tenantId = await AddTenantAsync(host);
        await AddPasswordUserAsync(host, tenantId, UserRole.Admin(), issueTemporary: DateTimeOffset.UtcNow - PasswordCredential.TemporaryLifetime - TimeSpan.FromMinutes(1));
        await host.WithDatabaseAsync(async db =>
        {
            var settings = await db.LoginSettings.SingleAsync();
            settings.TurnPasswordsOff(settings.Version, eachTenantHasALinkedAdmin: true, null!);
            return await db.SaveChangesAsync();
        });
        await host.Services.GetRequiredService<LoginModes>().ReloadAsync(CancellationToken.None);

        // Act
        using var browser = new AuthBrowser(host);
        var pageWithOnlyAnExpiredPassword = await (await browser.GetAsync(AuthRoutes.Login)).Content.ReadAsStringAsync();
        await AddPasswordUserAsync(host, tenantId, UserRole.Admin(), issueTemporary: DateTimeOffset.UtcNow);
        var status = await StatusAsync(host);
        var page = await browser.GetAsync(AuthRoutes.Login);
        var form = await browser.GetAsync($"{AuthRoutes.Login}?{AuthPages.TemporaryPasswordQuery}=1");
        var formText = await form.Content.ReadAsStringAsync();

        // Assert
        using (var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("state").GetString().ShouldBe("no_login_method");
            body.RootElement.GetProperty("message").GetString().ShouldBe(AuthPages.NoLoginMethodMessage);
            body.RootElement.GetProperty("modes").GetArrayLength().ShouldBe(0);
        }
        pageWithOnlyAnExpiredPassword.ShouldContain(AuthPages.NoLoginMethodMessage);
        pageWithOnlyAnExpiredPassword.ShouldNotContain(AuthPages.TemporaryPasswordLabel);
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var pageText = await page.Content.ReadAsStringAsync();
        pageText.ShouldContain(AuthPages.NoLoginMethodMessage);
        pageText.ShouldContain(AuthPages.TemporaryPasswordLabel);
        pageText.ShouldNotContain($"action=\"{AuthRoutes.PasswordLogin}\"");
        formText.ShouldContain($"action=\"{AuthRoutes.PasswordLogin}\"");
        await ResetAsync(host);
    }

    [Test]
    [Property("Rule", "IDP-16")]
    public async Task while_passwords_are_off_the_claim_owner_file_still_signs_the_operator_in_and_keeps_passwords_off()
    {
        // Arrange
        await ResetAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        await TurnOffAsync(browser);
        var claimFile = Host.Services.GetRequiredService<OwnerClaim>().FilePath;
        await File.WriteAllTextAsync(claimFile, "");
        using var operatorBrowser = new AuthBrowser(Host);
        var loginName = $"owner-{Guid.NewGuid():N}";

        // Act
        var page = await (await operatorBrowser.GetAsync(AuthRoutes.Login)).Content.ReadAsStringAsync();
        var state = await operatorBrowser.FormStateAsync(AuthRoutes.PasswordClaimOwner);
        var claimed = await operatorBrowser.PostAsync(AuthRoutes.PasswordClaimOwner,
            [("state", state), ("loginName", loginName), ("password", $"the owner passphrase {Guid.NewGuid():N}")]);

        // Assert
        page.ShouldContain(AuthPages.OwnerClaimLabel);
        claimed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        operatorBrowser.SessionCookie.ShouldNotBeNull();
        File.Exists(claimFile).ShouldBeFalse();
        Modes.PasswordsAreOn.ShouldBeFalse();
        (await Host.WithDatabaseAsync(db => db.LoginSettings.SingleAsync())).PasswordLogin.ShouldBe(PasswordLoginState.Off);
        (await StatusModeIdsAsync(Host)).ShouldNotContain(PasswordLoginMode.ModeId);
    }
}
