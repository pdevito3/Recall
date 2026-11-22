namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.LoginProviders;
using Recall.Server.Domain.LoginProviders.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

/// <summary>The login providers that an Admin adds in the app. A row is install-wide, so the tests of this class run one at a time on hosts of their own.</summary>
[NotInParallel(nameof(LoginProviderTests))]
public class LoginProviderTests
{
    private const string Providers = "/api/v1/login-providers";
    private const string RowId = "row";

    private static AuthFlowFactory Host => AuthHosts.LoginProviders;

    private static async Task ClearRowsAsync(AuthFlowFactory host)
    {
        await host.WithDatabaseAsync(async db =>
        {
            db.LoginProviders.RemoveRange(await db.LoginProviders.ToListAsync());
            // A link that an earlier test left to the row issuer would make the delete guard of a later test keep its Admin
            db.Set<ExternalLogin>().RemoveRange(await db.Set<ExternalLogin>()
                .Where(l => l.Issuer == AuthFlowFactory.RowIssuer || l.Issuer == AuthFlowFactory.RowIssuer + "/").ToListAsync());
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

    private static object RowBody(string id = RowId, string issuer = AuthFlowFactory.RowIssuer, string? secret = null) => new
    {
        id,
        displayName = "Row provider",
        issuer,
        clientId = StubOidcProvider.ClientId,
        clientSecret = secret ?? Host.RowProvider.ClientSecret,
        allowHttp = false,
    };

    private static async Task<HttpResponseMessage> SendAsync(AuthBrowser browser, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
    }

    private static async Task<JsonDocument> JsonOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = await JsonOf(response);
        return problem.RootElement.GetProperty("detail").GetString();
    }

    private static async Task<IReadOnlyList<string>> StatusModeIdsAsync(AuthFlowFactory host)
    {
        using var client = host.NewClient();
        using var status = JsonDocument.Parse(await client.GetStringAsync(AuthRoutes.Status));
        return status.RootElement.GetProperty("modes").EnumerateArray().Select(mode => mode.GetProperty("id").GetString()!).ToList();
    }

    private static async Task<string?> StatusStateOfAsync(AuthFlowFactory host, string id)
    {
        using var client = host.NewClient();
        using var status = JsonDocument.Parse(await client.GetStringAsync(AuthRoutes.Status));
        return status.RootElement.GetProperty("modes").EnumerateArray()
            .Where(mode => mode.GetProperty("id").GetString() == id)
            .Select(mode => mode.GetProperty("state").GetString())
            .SingleOrDefault();
    }

    private static Task<int> LinkAsync(string userId, string issuer, string subject)
        => Host.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<ExternalLogin>().Add(new ExternalLogin { Issuer = issuer, Subject = subject, UserId = userId, CreatedAt = now, LastLoginAt = now });
            return await db.SaveChangesAsync();
        });

    [Test]
    [Property("Rule", "IDP-1")]
    public async Task an_added_provider_is_on_the_status_and_signs_a_linked_person_in_with_no_restart()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = $"row-{Guid.NewGuid():N}";
        await LinkAsync(admin.UserId, AuthFlowFactory.RowIssuer, subject);
        var before = await StatusModeIdsAsync(Host);

        // Act
        var added = await SendAsync(browser, HttpMethod.Post, Providers, RowBody());
        var after = await StatusModeIdsAsync(Host);
        using var person = new AuthBrowser(Host);
        var start = await person.GetAsync($"/auth/oidc/{RowId}/start?client=web");
        var authorize = AuthorizeRequest.Of(start.Headers.Location!);
        var callback = await person.CallbackAsync(authorize.State, Host.RowProvider.Approve(authorize, new StubLogin(subject)), AuthFlowFactory.RowIssuer);

        // Assert
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        before.ShouldNotContain(RowId);
        after.ShouldBe([AuthFlowFactory.ModeId, RowId]);
        start.Headers.Location!.OriginalString.ShouldStartWith(AuthFlowFactory.RowIssuer + "/authorize");
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        person.SessionCookie.ShouldNotBeNull();
        (await person.UserOfSessionAsync()).ShouldBe(await browser.UserOfSessionAsync());
    }

    [Test]
    [Property("Rule", "IDP-1")]
    public async Task a_login_in_flight_whose_provider_is_deleted_ends_at_its_callback_with_invalid_response()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        (await SendAsync(browser, HttpMethod.Post, Providers, RowBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var person = new AuthBrowser(Host);
        var app = NativeApp.New();
        var start = await person.GetAsync(app.StartUrl.Replace($"/oidc/{AuthFlowFactory.ModeId}/", $"/oidc/{RowId}/", StringComparison.Ordinal));
        var authorize = AuthorizeRequest.Of(start.Headers.Location!);
        var code = Host.RowProvider.Approve(authorize, new StubLogin($"row-{Guid.NewGuid():N}"));

        // Act
        var deleted = await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}");
        var callback = await person.CallbackAsync(authorize.State, code, AuthFlowFactory.RowIssuer);

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusModeIdsAsync(Host)).ShouldNotContain(RowId);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = callback.Headers.Location!.OriginalString;
        location.ShouldStartWith(app.Redirect + "?");
        QueryHelpers.ParseQuery(location[app.Redirect.Length..])["error"].ToString().ShouldBe("invalid_response");
    }

    [Test]
    [Property("Rule", "IDP-3")]
    public async Task a_provider_with_the_id_or_the_issuer_of_another_provider_gets_a_conflict_and_nothing_is_saved()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        (await SendAsync(browser, HttpMethod.Post, Providers, RowBody())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var idOfTheEnvironment = await SendAsync(browser, HttpMethod.Post, Providers, RowBody(id: AuthFlowFactory.ModeId, issuer: "https://elsewhere.example"));
        var issuerOfTheEnvironment = await SendAsync(browser, HttpMethod.Post, Providers, RowBody(id: "again", issuer: StubOidcProvider.Issuer + "/"));
        var idOfARow = await SendAsync(browser, HttpMethod.Post, Providers, RowBody(issuer: "https://elsewhere.example"));
        var issuerOfARow = await SendAsync(browser, HttpMethod.Post, Providers, RowBody(id: "again"));

        // Assert
        idOfTheEnvironment.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(idOfTheEnvironment)).ShouldBe(LoginProvider.IdInUseMessage);
        issuerOfTheEnvironment.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(issuerOfTheEnvironment)).ShouldBe(LoginProvider.IssuerInUseMessage);
        idOfARow.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(idOfARow)).ShouldBe(LoginProvider.IdInUseMessage);
        issuerOfARow.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(issuerOfARow)).ShouldBe(LoginProvider.IssuerInUseMessage);
        (await Host.WithDatabaseAsync(db => db.LoginProviders.CountAsync())).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IDP-3")]
    public async Task a_row_with_the_issuer_of_an_entry_of_the_environment_is_replaced_and_not_on_the_status()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        await Host.WithDatabaseAsync(async db =>
        {
            var form = new LoginProviderForm("Old", StubOidcProvider.Issuer, "recall", false, null);
            var secret = Host.Services.GetRequiredService<LoginProviderSecret>().Protect("a-secret");
            db.LoginProviders.Add(LoginProvider.Add(form, "old", secret, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        await Host.Services.GetRequiredService<LoginModes>().ReloadAsync(CancellationToken.None);

        // Act
        using var list = await JsonOf(await SendAsync(browser, HttpMethod.Get, Providers));

        // Assert
        list.RootElement.GetProperty("items")[0].GetProperty("state").GetString().ShouldBe(nameof(LoginProviderState.ReplacedByEnvironment));
        (await StatusModeIdsAsync(Host)).ShouldBe([AuthFlowFactory.ModeId]);
    }

    [Test]
    [Property("Rule", "IDP-4")]
    [Property("Rule", "IDP-8")]
    public async Task the_list_holds_no_secret_and_gives_the_callback_url_of_the_status()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        var added = await SendAsync(browser, HttpMethod.Post, Providers, RowBody());
        using var client = Host.NewClient();
        using var status = JsonDocument.Parse(await client.GetStringAsync(AuthRoutes.Status));

        // Act
        var list = await SendAsync(browser, HttpMethod.Get, Providers);
        var text = await list.Content.ReadAsStringAsync();

        // Assert
        (await added.Content.ReadAsStringAsync()).ShouldNotContain(Host.RowProvider.ClientSecret);
        text.ShouldNotContain(Host.RowProvider.ClientSecret);
        using var body = JsonDocument.Parse(text);
        var item = body.RootElement.GetProperty("items")[0];
        item.TryGetProperty("clientSecret", out _).ShouldBeFalse();
        item.GetProperty("secretState").GetString().ShouldBe(nameof(LoginProviderSecretState.Readable));
        item.GetProperty("state").GetString().ShouldBe(nameof(LoginProviderState.Usable));
        body.RootElement.GetProperty("environmentProviders")[0].GetProperty("id").GetString().ShouldBe(AuthFlowFactory.ModeId);
        body.RootElement.GetProperty("callbackUrl").GetString().ShouldBe(status.RootElement.GetProperty("callbackUrl").GetString());
    }

    [Test]
    [Property("Rule", "IDP-4")]
    public async Task an_update_with_no_secret_keeps_the_stored_secret_so_the_login_still_works()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var subject = $"row-{Guid.NewGuid():N}";
        await LinkAsync(admin.UserId, AuthFlowFactory.RowIssuer, subject);
        using var added = await JsonOf(await SendAsync(browser, HttpMethod.Post, Providers, RowBody()));
        var version = added.RootElement.GetProperty("version").GetUInt32();

        // Act
        var updated = await SendAsync(browser, HttpMethod.Put, $"{Providers}/{RowId}", new
        {
            displayName = "Renamed",
            clientId = StubOidcProvider.ClientId,
            clientSecret = "",
            allowHttp = false,
            version,
        });
        using var person = new AuthBrowser(Host);
        var authorize = AuthorizeRequest.Of((await person.GetAsync($"/auth/oidc/{RowId}/start?client=web")).Headers.Location!);
        var callback = await person.CallbackAsync(authorize.State, Host.RowProvider.Approve(authorize, new StubLogin(subject)), AuthFlowFactory.RowIssuer);

        // Assert
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await JsonOf(updated);
        body.RootElement.GetProperty("displayName").GetString().ShouldBe("Renamed");
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        person.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "IDP-6")]
    public async Task an_update_with_another_issuer_gets_a_validation_error()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        using var added = await JsonOf(await SendAsync(browser, HttpMethod.Post, Providers, RowBody()));

        // Act
        var updated = await SendAsync(browser, HttpMethod.Put, $"{Providers}/{RowId}", new
        {
            displayName = "Row provider",
            issuer = "https://elsewhere.example",
            clientId = StubOidcProvider.ClientId,
            allowHttp = false,
            version = added.RootElement.GetProperty("version").GetUInt32(),
        });

        // Assert
        updated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DetailOf(updated)).ShouldBe(LoginProvider.IssuerIsFixedMessage);
    }

    [Test]
    [Property("Rule", "IDP-5")]
    public async Task a_row_whose_secret_the_key_ring_cannot_read_is_not_on_the_status_and_its_start_gets_not_found()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        await Host.WithDatabaseAsync(async db =>
        {
            var form = new LoginProviderForm("Lost", AuthFlowFactory.RowIssuer, StubOidcProvider.ClientId, false, null);
            db.LoginProviders.Add(LoginProvider.Add(form, RowId, "protected under another ring", DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        await Host.Services.GetRequiredService<LoginModes>().ReloadAsync(CancellationToken.None);

        // Act
        var modes = await StatusModeIdsAsync(Host);
        var start = await new AuthBrowser(Host).GetAsync($"/auth/oidc/{RowId}/start?client=web");
        using var list = await JsonOf(await SendAsync(browser, HttpMethod.Get, Providers));

        // Assert
        modes.ShouldNotContain(RowId);
        start.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var item = list.RootElement.GetProperty("items")[0];
        item.GetProperty("secretState").GetString().ShouldBe(nameof(LoginProviderSecretState.Unreadable));
        item.GetProperty("state").GetString().ShouldBe(nameof(LoginProviderState.SecretUnreadable));
    }

    [Test]
    [Property("Rule", "IDP-7")]
    public async Task a_test_answers_with_the_issuer_or_the_failure_and_saves_nothing()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        object Form(string issuer) => new { issuer, allowHttp = false };

        // Act
        using var ok = await JsonOf(await SendAsync(browser, HttpMethod.Post, $"{Providers}/test", Form(AuthFlowFactory.RowIssuer)));
        using var notDiscovery = await JsonOf(await SendAsync(browser, HttpMethod.Post, $"{Providers}/test", Form(AuthFlowFactory.RowIssuer + "/nothing")));
        Host.RowProvider.CodeChallengeMethods = ["plain"];
        JsonDocument noS256;
        try
        {
            noS256 = await JsonOf(await SendAsync(browser, HttpMethod.Post, $"{Providers}/test", Form(AuthFlowFactory.RowIssuer)));
        }
        finally
        {
            Host.RowProvider.CodeChallengeMethods = ["S256"];
        }

        // Assert
        ok.RootElement.GetProperty("ok").GetBoolean().ShouldBeTrue();
        ok.RootElement.GetProperty("issuer").GetString().ShouldBe(AuthFlowFactory.RowIssuer);
        notDiscovery.RootElement.GetProperty("failure").GetProperty("message").GetString().ShouldBe(TestLoginProvider.NotDiscoveryMessage);
        noS256.RootElement.GetProperty("failure").GetProperty("message").GetString().ShouldBe(TestLoginProvider.NoS256Message);
        noS256.Dispose();
        (await Host.WithDatabaseAsync(db => db.LoginProviders.CountAsync())).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-18")]
    public async Task an_add_an_update_and_a_delete_record_their_actions_with_no_secret()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        const string secret = "a-new-client-secret-for-the-audit";
        using var added = await JsonOf(await SendAsync(browser, HttpMethod.Post, Providers, RowBody()));

        // Act
        (await SendAsync(browser, HttpMethod.Put, $"{Providers}/{RowId}", new
        {
            displayName = "Row provider",
            clientId = StubOidcProvider.ClientId,
            clientSecret = secret,
            allowHttp = false,
            version = added.RootElement.GetProperty("version").GetUInt32(),
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert
        var entries = await Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == admin.TenantId && e.Action != null && e.Action.StartsWith("login_provider_"))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync());
        entries.Select(e => e.Action).ShouldBe(["login_provider_added", "login_provider_updated", "login_provider_deleted"]);
        entries.ShouldAllBe(e => e.ActorUserId == admin.UserId && e.Data.Contains(RowId));
        entries.ShouldAllBe(e => !e.Data.Contains(secret) && !e.Data.Contains(Host.RowProvider.ClientSecret));
    }

    [Test]
    public async Task a_delete_that_takes_the_last_way_in_of_a_tenant_gets_a_conflict_and_keeps_the_provider()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, _) = await SignedInAdminAsync();
        using var signedIn = browser;
        (await SendAsync(browser, HttpMethod.Post, Providers, RowBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var (stranded, _) = await PatPeople.AddTenantAsync(Host);
        await Host.WithDatabaseAsync(async db =>
        {
            var link = await db.Set<ExternalLogin>().SingleAsync(l => l.UserId == stranded.UserId);
            db.Set<ExternalLogin>().Remove(link);
            return await db.SaveChangesAsync();
        });
        await LinkAsync(stranded.UserId, AuthFlowFactory.RowIssuer, $"row-{Guid.NewGuid():N}");

        // Act
        var refused = await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}");
        var modesAfterRefusal = await StatusModeIdsAsync(Host);
        await LinkAsync(stranded.UserId, StubOidcProvider.Issuer, $"pat-{Guid.NewGuid():N}");
        var allowed = await SendAsync(browser, HttpMethod.Delete, $"{Providers}/{RowId}");

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(refused)).ShouldBe(LastAdmin.RefusedMessage);
        modesAfterRefusal.ShouldContain(RowId);
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task an_admin_whose_only_link_names_a_deleted_provider_does_not_count_as_another_admin()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        var other = await PatPeople.AddPersonAsync(Host, admin.TenantId, UserRole.Admin());
        await Host.WithDatabaseAsync(async db =>
        {
            var link = await db.Set<ExternalLogin>().SingleAsync(l => l.UserId == other.UserId);
            db.Set<ExternalLogin>().Remove(link);
            db.Set<ExternalLogin>().Add(link with { Issuer = "https://gone.example" });
            return await db.SaveChangesAsync();
        });

        // Act
        var demote = await SendAsync(browser, HttpMethod.Put, $"/api/v1/users/{admin.UserId}/role", new { role = "User" });

        // Assert
        demote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(demote)).ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    public async Task a_personal_access_token_and_a_member_with_no_permission_get_forbidden()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, admin, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var client = PatPeople.ClientWith(Host, token.Value);
        using var memberBrowser = new AuthBrowser(Host);
        await memberBrowser.LoginWebAsync(member.Subject);

        // Act
        var list = await client.GetAsync(Providers);
        var add = await client.PostAsJsonAsync(Providers, RowBody());
        var test = await client.PostAsJsonAsync($"{Providers}/test", new { issuer = AuthFlowFactory.RowIssuer, allowHttp = false });
        var memberAdd = await SendAsync(memberBrowser, HttpMethod.Post, Providers, RowBody());

        // Assert
        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        add.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        test.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        memberAdd.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Host.WithDatabaseAsync(db => db.LoginProviders.CountAsync())).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IDP-17")]
    public async Task a_usable_row_closes_the_setup_of_an_install_with_no_provider_in_the_environment()
    {
        // Arrange
        var host = AuthHosts.LoginProvidersNoEnvironment;
        await ClearRowsAsync(host);
        var admin = await PasswordPeople.AddAsync(host);
        using var browser = new AuthBrowser(host);
        (await browser.SignInAsync(admin.LoginName, admin.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var before = await browser.GetAsync(AuthRoutes.PasswordSetup);

        // Act
        var added = await SendAsync(browser, HttpMethod.Post, Providers, new
        {
            id = RowId,
            displayName = "Row provider",
            issuer = AuthFlowFactory.RowIssuer,
            clientId = StubOidcProvider.ClientId,
            clientSecret = host.RowProvider.ClientSecret,
            allowHttp = false,
        });
        var after = await browser.GetAsync(AuthRoutes.PasswordSetup);

        // Assert
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        before.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
        after.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await ClearRowsAsync(host);
    }

    [Test]
    [Property("Rule", "IDP-1")]
    [Property("Rule", "IDP-17")]
    public async Task a_fresh_server_on_a_database_with_a_usable_row_lists_it_and_closes_the_setup()
    {
        // Arrange
        var first = AuthHosts.StartUntilTheEnd(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = "100000" }) { NoProvider = true });
        await first.WithDatabaseAsync(async db =>
        {
            var form = new LoginProviderForm("Row provider", AuthFlowFactory.RowIssuer, StubOidcProvider.ClientId, false, null);
            var secret = first.Services.GetRequiredService<LoginProviderSecret>().Protect(first.RowProvider.ClientSecret);
            db.LoginProviders.Add(LoginProvider.Add(form, RowId, secret, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        // Act
        var restarted = AuthHosts.StartUntilTheEnd(new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:StartPermitsPerWindow"] = "100000",
            [$"ConnectionStrings:{DatabaseConsts.DatabaseName}"] = first.ConnectionString,
        }) { NoProvider = true });
        var modes = await StatusModeIdsAsync(restarted);
        using var browser = new AuthBrowser(restarted);
        var setup = await browser.GetAsync(AuthRoutes.PasswordSetup);

        // Assert
        modes.ShouldContain(RowId);
        setup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task a_link_whose_issuer_has_a_trailing_slash_counts_for_a_row_whose_issuer_has_none()
    {
        // Arrange
        await ClearRowsAsync(Host);
        var (browser, admin) = await SignedInAdminAsync();
        using var signedIn = browser;
        (await SendAsync(browser, HttpMethod.Post, Providers, RowBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await PatPeople.AddPersonAsync(Host, admin.TenantId, UserRole.Admin());
        await Host.WithDatabaseAsync(async db =>
        {
            var link = await db.Set<ExternalLogin>().SingleAsync(l => l.UserId == other.UserId);
            db.Set<ExternalLogin>().Remove(link);
            db.Set<ExternalLogin>().Add(link with { Issuer = AuthFlowFactory.RowIssuer + "/" });
            return await db.SaveChangesAsync();
        });

        // Act
        var demote = await SendAsync(browser, HttpMethod.Put, $"/api/v1/users/{admin.UserId}/role", new { role = "User" });

        // Assert
        demote.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_row_on_a_loopback_issuer_is_unreachable_unless_the_operator_allows_loopback_providers()
    {
        // Arrange
        await using var idp = await LoopbackIdp.StartAsync();
        var allowed = AuthHosts.LoginProvidersNoEnvironment;
        await ClearRowsAsync(Host);
        await ClearRowsAsync(allowed);
        var (refusingBrowser, _) = await SignedInAdminAsync();
        using var refusing = refusingBrowser;
        var person = await PasswordPeople.AddAsync(allowed);
        using var allowing = new AuthBrowser(allowed);
        (await allowing.SignInAsync(person.LoginName, person.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var form = new { issuer = idp.Issuer, allowHttp = true };
        var row = new { id = "loop", displayName = "Loop", issuer = idp.Issuer, clientId = "recall", clientSecret = "a-secret", allowHttp = true };

        // Act
        using var refusedTest = await JsonOf(await SendAsync(refusing, HttpMethod.Post, $"{Providers}/test", form));
        (await SendAsync(refusing, HttpMethod.Post, Providers, row)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refusedState = await StatusStateOfAsync(Host, "loop");
        using var allowedTest = await JsonOf(await SendAsync(allowing, HttpMethod.Post, $"{Providers}/test", form));
        (await SendAsync(allowing, HttpMethod.Post, Providers, row)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var allowedState = await StatusStateOfAsync(allowed, "loop");

        // Assert
        refusedTest.RootElement.GetProperty("failure").GetProperty("kind").GetString().ShouldBe(nameof(LoginProviderTestFailure.Unreachable));
        refusedTest.RootElement.GetProperty("failure").GetProperty("message").GetString().ShouldBe(TestLoginProvider.UnreachableMessage);
        refusedState.ShouldBe("unreachable");
        allowedTest.RootElement.GetProperty("ok").GetBoolean().ShouldBeTrue();
        allowedTest.RootElement.GetProperty("issuer").GetString().ShouldBe(idp.Issuer);
        allowedState.ShouldBe("ready");
        await ClearRowsAsync(Host);
        await ClearRowsAsync(allowed);
    }
}
