namespace Recall.FunctionalTests.Auth;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Entrypoints.Jobs;
using Shouldly;

public class PersonalAccessTokenTests
{
    private const string Me = "/auth/me";
    private static readonly string Tenants = $"/{ApiRoutes.Tenants.GetList}";
    private static readonly TokenGrant NoPermissions = new([], JournalAccess.None);

    private static AuthFlowFactory Host => AuthHosts.Open;

    [Test]
    [Property("Rule", "PAT-18")]
    public async Task a_token_reaches_the_api_and_mcp_as_its_person_and_lists_only_the_tools_that_both_its_grant_and_its_person_hold()
    {
        // Arrange
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var adminPlain = await PatPeople.StartTokenAsync(Host, admin, NoPermissions);
        var adminManage = await PatPeople.StartTokenAsync(Host, admin, new TokenGrant([Permission.ManageTenant()], JournalAccess.None));
        var memberManage = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([Permission.ManageTenant()], JournalAccess.None));

        // Act
        using var client = PatPeople.ClientWith(Host, adminPlain.Value);
        var tenants = await client.GetAsync(Tenants);
        var plainTools = await ToolsOfAsync(adminPlain.Value);
        var adminTools = await ToolsOfAsync(adminManage.Value);
        var memberTools = await ToolsOfAsync(memberManage.Value);
        await using var mcp = await PatPeople.McpAsync(client);
        var tenant = await mcp.CallToolAsync("get_tenant", new Dictionary<string, object?> { ["id"] = admin.TenantId });

        // Assert
        tenants.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await tenants.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        listed.ShouldBe([admin.TenantId]);
        plainTools.ShouldContain("get_tenant");
        plainTools.ShouldContain("list_tenants");
        plainTools.ShouldNotContain("update_tenant");
        adminTools.ShouldContain("update_tenant");
        memberTools.ShouldNotContain("update_tenant");
        tenant.IsError.ShouldNotBe(true);
        tenant.StructuredContent!.Value.GetProperty("id").GetString().ShouldBe(admin.TenantId);
    }

    [Test]
    [Property("Rule", "PAT-9")]
    public async Task a_revoked_token_gets_401_on_its_next_request_on_the_api_and_on_mcp()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        using var client = PatPeople.ClientWith(Host, token.Value);
        var before = await client.GetAsync(Tenants);
        var user = await Host.WithDatabaseAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == member.UserId));

        // Act
        await Host.WithStoreAsync(store => store.RevokePersonalAccessTokenAsync(user, token.Token.Id));
        var api = await client.GetAsync(Tenants);
        var mcp = await client.PostAsync("/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"));

        // Assert
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        mcp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Property("Rule", "PAT-11")]
    public async Task a_token_works_as_a_bearer_with_no_csrf_header_and_its_value_in_the_cookie_authenticates_nothing()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        using var browser = new AuthBrowser(Host);
        browser.SetCookie(browser.SessionCookieName, token.Value);

        // Act
        var asBearer = await browser.GetAsync(Tenants, authorization: $"Bearer {token.Value}");
        var cookieOnApi = await browser.GetAsync(Tenants, csrf: true);
        var cookieOnMe = await browser.GetAsync(Me, csrf: true);
        var cookieOnRevokeOthers = await browser.PostFormAsync(AuthRoutes.RevokeOthers, [], csrf: true);

        // Assert
        asBearer.StatusCode.ShouldBe(HttpStatusCode.OK);
        cookieOnApi.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        cookieOnMe.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        cookieOnRevokeOthers.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PatPeople.RowAsync(Host, token.Token.Id)).RevokedAt.ShouldBeNull();
    }

    [Test]
    public async Task me_answers_a_token_with_its_person_its_id_and_the_kind_pat()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        using var client = PatPeople.ClientWith(Host, token.Value);

        // Act
        var me = await client.GetAsync(Me);

        // Assert
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        (body.GetProperty("sub").GetString(), body.GetProperty("sid").GetString(), body.GetProperty("client_kind").GetString())
            .ShouldBe((member.UserId, token.Token.Id, "pat"));
    }

    [Test]
    [Property("Rule", "PAT-10")]
    public async Task revoke_others_refuses_a_token_and_from_a_session_revokes_each_token_of_its_person()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var first = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        var second = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(member.Subject);
        using var firstClient = PatPeople.ClientWith(Host, first.Value);
        using var secondClient = PatPeople.ClientWith(Host, second.Value);

        // Act
        var byToken = await firstClient.PostAsync(AuthRoutes.RevokeOthers, null);
        var secondAfterRefusal = await secondClient.GetAsync(Tenants);
        var bySession = await browser.PostFormAsync(AuthRoutes.RevokeOthers, [], csrf: true);
        var firstAfter = await firstClient.GetAsync(Tenants);
        var secondAfter = await secondClient.GetAsync(Tenants);

        // Assert
        byToken.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        secondAfterRefusal.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        bySession.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        firstAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        secondAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task logout_with_a_token_ends_nothing()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        using var client = PatPeople.ClientWith(Host, token.Value);

        // Act
        var logout = await client.PostAsync(AuthRoutes.Logout, null);
        var after = await client.GetAsync(Tenants);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Property("Rule", "PAT-4")]
    public async Task a_permission_that_the_person_loses_refuses_the_next_call_of_the_token()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        await SetPermissionAsync(member, Permission.ReadUsers(), held: true);
        var token = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([Permission.ReadUsers()], JournalAccess.None));
        using var client = PatPeople.ClientWith(Host, token.Value);
        var before = await client.GetAsync($"/{ApiRoutes.Users.GetList}");

        // Act
        await SetPermissionAsync(member, Permission.ReadUsers(), held: false);
        var after = await client.GetAsync($"/{ApiRoutes.Users.GetList}");

        // Assert
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(after)).ShouldBe($"You cannot {Permission.ReadUsers().Words}. Ask an admin for access.");
    }

    [Test]
    [Property("Rule", "PAT-12")]
    public async Task a_token_of_the_owner_with_no_grant_to_read_users_cannot_read_its_own_record_or_pass_the_owner_gate()
    {
        // Arrange
        var host = AuthHosts.Closed;
        using var browser = new AuthBrowser(host);
        await browser.LoginWebAsync("owner");
        var ownerId = await host.WithDatabaseAsync(db => db.Set<ExternalLogin>().IgnoreQueryFilters()
            .Where(link => link.Subject == "owner").Select(link => link.UserId).SingleAsync());
        var owner = new PatPerson("owner", ownerId, "");
        var grant = new TokenGrant(Permission.GetAll().Where(p => p != Permission.ReadUsers()), JournalAccess.ReadWrite);
        var token = await PatPeople.StartTokenAsync(host, owner, grant);
        var full = await PatPeople.StartTokenAsync(host, owner, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var client = PatPeople.ClientWith(host, token.Value);
        using var fullClient = PatPeople.ClientWith(host, full.Value);

        // Act
        var ownRecord = await client.GetAsync($"/{ApiRoutes.Users.GetRecord(ownerId)}");
        var bundle = await client.GetAsync($"/{ApiRoutes.SupportBundle}");
        var dashboard = await client.GetAsync(JobDashboard.Path);
        var ownRecordWithTheGrant = await fullClient.GetAsync($"/{ApiRoutes.Users.GetRecord(ownerId)}");
        var bundleWithTheFullGrant = await fullClient.GetAsync($"/{ApiRoutes.SupportBundle}");

        // Assert
        ownRecord.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(ownRecord)).ShouldBe($"This personal access token has no grant to {Permission.ReadUsers().Words}. Make a token that has it.");
        bundle.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        dashboard.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ownRecordWithTheGrant.StatusCode.ShouldBe(HttpStatusCode.OK);
        bundleWithTheFullGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [NotInParallel(nameof(AuthHosts.KeepAlive))]
    [Property("Rule", "PAT-16")]
    public async Task a_busy_writer_line_delays_no_request_on_a_keep_alive_connection_and_a_later_request_marks_the_use()
    {
        // Arrange
        var host = AuthHosts.KeepAlive;
        var (_, member) = await PatPeople.AddTenantAsync(host);
        var warm = await PatPeople.StartTokenAsync(host, member, NoPermissions, name: "Warm");
        var token = await PatPeople.StartTokenAsync(host, member, NoPermissions);
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 1, PooledConnectionLifetime = Timeout.InfiniteTimeSpan };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
        (await SendAsync(client, warm.Value)).ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(async () => (await PatPeople.RowAsync(host, warm.Token.Id)).LastUsedAt is not null);
        var hold = AppSessionHandler.UseMarkWait * 3;

        // Act
        var (first, second) = await WithTheLineHeldAsync(host, hold, async () =>
        {
            var firstWatch = Stopwatch.StartNew();
            (await SendAsync(client, token.Value)).ShouldBe(HttpStatusCode.OK);
            var firstTime = firstWatch.Elapsed;
            var secondWatch = Stopwatch.StartNew();
            (await SendAsync(client, token.Value)).ShouldBe(HttpStatusCode.OK);
            return (firstTime, secondWatch.Elapsed);
        });
        var markedWhileHeld = (await PatPeople.RowAsync(host, token.Token.Id)).LastUsedAt;
        (await SendAsync(client, token.Value)).ShouldBe(HttpStatusCode.OK);

        // Assert
        first.ShouldBeLessThan(AppSessionHandler.UseMarkWait * 0.75);
        second.ShouldBeLessThan(AppSessionHandler.UseMarkWait * 0.75);
        markedWhileHeld.ShouldBeNull();
        await EventuallyAsync(async () => (await PatPeople.RowAsync(host, token.Token.Id)).LastUsedAt is not null);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Tenants);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // The line stays held until the hold ends, also after the requests answer, so each mark that they start waits past its limit.
    private static async Task<T> WithTheLineHeldAsync<T>(AuthFlowFactory host, TimeSpan hold, Func<Task<T>> work)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var held = Stopwatch.StartNew();
        using (await SqliteWriterLine.WaitForTurnAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database, CancellationToken.None))
        {
            var result = await work();
            if (hold - held.Elapsed is { } rest && rest > TimeSpan.Zero)
                await Task.Delay(rest);
            return result;
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> done)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await done())
            await Task.Delay(TimeSpan.FromMilliseconds(20), limit.Token);
    }

    private static async Task<IReadOnlyList<string>> ToolsOfAsync(string value)
    {
        using var client = PatPeople.ClientWith(Host, value);
        await using var mcp = await PatPeople.McpAsync(client);
        return (await mcp.ListToolsAsync()).Select(tool => tool.Name).ToList();
    }

    private static Task<bool> SetPermissionAsync(PatPerson person, Permission permission, bool held)
        => Host.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.Include(u => u.UserPermissions).SingleAsync(u => u.Id == person.UserId);
            if (held)
                user.AddPermission(permission);
            else
                user.RemovePermission(permission);
            await db.SaveChangesAsync();
            return true;
        });

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }
}
