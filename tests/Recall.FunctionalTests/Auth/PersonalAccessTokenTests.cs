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
using Recall.Server.Auth;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Models;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Entrypoints.Jobs;
using Shouldly;

public class PersonalAccessTokenTests
{
    private const string Me = "/auth/me";
    private static readonly string Tenants = $"/{ApiRoutes.Tenants.GetList}";
    private static readonly TokenGrant NoPermissions = new([], JournalAccess.None);

    private const string Tokens = "/api/v1/personal-access-tokens";
    private const string NotAPerson = "Only a person signed in to Recall can do this.";

    private static AuthFlowFactory Host => AuthHosts.Open;

    private static string TokensOf(string userId) => $"/api/v1/users/{userId}/personal-access-tokens";

    private static string RevokeOf(string tokenId) => $"{Tokens}/{tokenId}/revoke";

    private static object Body(DateTimeOffset? expiresAt = null, bool? neverExpires = null, string[]? permissions = null, bool read = false, bool write = false)
        => new { name = "Laptop", permissions = permissions ?? [], readJournal = read, writeJournal = write, expiresAt, neverExpires };

    [Test]
    [Property("Rule", "PAT-1")]
    [Property("Rule", "PAT-9")]
    [Property("Rule", "PAT-18")]
    public async Task a_person_makes_a_token_uses_it_on_the_api_and_mcp_revokes_it_and_it_then_gets_401()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(member.Subject);

        // Act
        var created = await ApiAsync(browser, HttpMethod.Post, Tokens, Body(neverExpires: true));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var value = body.GetProperty("value").GetString()!;
        var tokenId = body.GetProperty("token").GetProperty("id").GetString()!;
        using var client = PatPeople.ClientWith(Host, value);
        var api = await client.GetAsync(Tenants);
        await using (var mcp = await PatPeople.McpAsync(client))
            (await mcp.ListToolsAsync()).Select(tool => tool.Name).ShouldContain("get_tenant");
        var list = await ApiAsync(browser, HttpMethod.Get, Tokens);
        var listText = await list.Content.ReadAsStringAsync();
        var revoked = await ApiAsync(browser, HttpMethod.Post, RevokeOf(tokenId));
        var again = await ApiAsync(browser, HttpMethod.Post, RevokeOf(tokenId));
        var after = await client.GetAsync(Tenants);

        // Assert
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        value.ShouldStartWith("rcl_pat_");
        tokenId.ShouldStartWith("pat_");
        body.GetProperty("token").GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("token").GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Revoke"]);
        api.StatusCode.ShouldBe(HttpStatusCode.OK);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        listText.ShouldContain(tokenId);
        listText.ShouldNotContain(value);
        listText.ShouldNotContain(value["rcl_pat_".Length..]);
        listText.ShouldNotContain(TokenHash.Of(value).Hex);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var listAfter = await (await ApiAsync(browser, HttpMethod.Get, Tokens)).Content.ReadFromJsonAsync<JsonElement>();
        var row = listAfter.EnumerateArray().Single(t => t.GetProperty("id").GetString() == tokenId);
        row.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.String);
        row.GetProperty("allowedActions").GetArrayLength().ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PAT-3")]
    public async Task a_grant_with_a_permission_that_the_person_does_not_hold_gives_403_and_makes_no_token()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(member.Subject);

        // Act
        var response = await ApiAsync(browser, HttpMethod.Post, Tokens, Body(neverExpires: true, permissions: ["read_users"]));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(response)).ShouldBe($"You cannot {Permission.ReadUsers().Words}. Ask an admin for access.");
        (await Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == member.UserId))).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "PAT-5")]
    [Property("Rule", "PAT-8")]
    [Arguments("neither", 422, "expiresAt")]
    [Arguments("both", 422, "expiresAt")]
    [Arguments("past", 422, "expiresAt")]
    [Arguments("at-never", 422, "expiresAt")]
    [Arguments("never-false", 422, "expiresAt")]
    [Arguments("write-without-read", 422, "journal")]
    [Arguments("future", 201, null)]
    [Arguments("never", 201, null)]
    public async Task a_create_needs_exactly_one_expiry_in_the_future_and_journal_write_needs_read(string body, int status, string? field)
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(member.Subject);
        var future = DateTimeOffset.UtcNow.AddDays(90);
        var sent = body switch
        {
            "neither" => Body(),
            "both" => Body(future, neverExpires: true),
            "past" => Body(DateTimeOffset.UtcNow.AddMinutes(-1)),
            "at-never" => Body(SessionPolicy.PersonalAccessTokenNever),
            "never-false" => Body(neverExpires: false),
            "write-without-read" => Body(future, write: true),
            "future" => Body(future, read: true, write: true),
            _ => Body(neverExpires: true, read: true),
        };

        // Act
        var response = await ApiAsync(browser, HttpMethod.Post, Tokens, sent);

        // Assert
        ((int)response.StatusCode).ShouldBe(status);
        if (field is not null)
        {
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            errors.GetProperty(field)[0].GetString().ShouldBe(field == "journal"
                ? "A token that changes your journal must also read it."
                : "Pick an expiry date in the future, or pick Never.");
        }
    }

    [Test]
    [Property("Rule", "PAT-2")]
    [Property("Rule", "PAT-10")]
    public async Task a_token_gets_403_on_each_route_that_makes_lists_or_revokes_tokens_and_on_revoke_others()
    {
        // Arrange
        var (admin, _) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, admin, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var client = PatPeople.ClientWith(Host, token.Value);

        // Act
        var create = await client.PostAsJsonAsync(Tokens, Body(neverExpires: true));
        var list = await client.GetAsync(Tokens);
        var userList = await client.GetAsync(TokensOf(admin.UserId));
        var revoke = await client.PostAsync(RevokeOf(token.Token.Id), null);
        var revokeOthers = await client.PostAsync(AuthRoutes.RevokeOthers, null);

        // Assert
        foreach (var response in new[] { create, list, userList, revoke })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await DetailOf(response)).ShouldBe(NotAPerson);
        }
        revokeOthers.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == admin.UserId))).ShouldBe(1);
        (await PatPeople.RowAsync(Host, token.Token.Id)).RevokedAt.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PAT-2")]
    public async Task a_provider_token_of_a_first_party_client_gets_403_on_each_route_that_makes_lists_or_revokes_tokens()
    {
        // Arrange
        var host = AuthHosts.FirstPartyClient;
        var (admin, _) = await PatPeople.AddTenantAsync(host);
        var subject = $"swg-{Guid.NewGuid():N}";
        var userId = await host.WithDatabaseAsync(async db =>
        {
            var user = User.Create(new UserForCreation
            {
                TenantId = admin.TenantId, FirstName = "Swagger", Identifier = subject, Username = subject[..20], Roles = [UserRole.Admin().Value],
            });
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        var token = await PatPeople.StartTokenAsync(host, new PatPerson(subject, userId, admin.TenantId), NoPermissions);
        using var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", host.Provider.SignJwt(new Dictionary<string, object> { ["sub"] = subject, ["client_id"] = AuthHosts.FirstPartyClientId }));

        // Act
        var ownRecord = await client.GetAsync($"/{ApiRoutes.Users.GetRecord(userId)}");
        var create = await client.PostAsJsonAsync(Tokens, Body(neverExpires: true, read: true, write: true));
        var list = await client.GetAsync(Tokens);
        var userList = await client.GetAsync(TokensOf(userId));
        var revoke = await client.PostAsync(RevokeOf(token.Token.Id), null);

        // Assert
        ownRecord.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var response in new[] { create, list, userList, revoke })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await DetailOf(response)).ShouldBe(NotAPerson);
        }
        (await host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == userId))).ShouldBe(1);
        (await PatPeople.RowAsync(host, token.Token.Id)).RevokedAt.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PAT-13")]
    public async Task an_admin_lists_and_revokes_the_tokens_of_the_tenant_and_a_person_of_another_tenant_is_404()
    {
        // Arrange
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var (_, otherMember) = await PatPeople.AddTenantAsync(Host);
        var reader = await PatPeople.AddPersonAsync(Host, admin.TenantId, [], Permission.ReadUsers());
        var memberToken = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        var otherToken = await PatPeople.StartTokenAsync(Host, otherMember, NoPermissions);
        using var adminBrowser = new AuthBrowser(Host);
        await adminBrowser.LoginWebAsync(admin.Subject);
        using var memberBrowser = new AuthBrowser(Host);
        await memberBrowser.LoginWebAsync(member.Subject);
        using var readerBrowser = new AuthBrowser(Host);
        await readerBrowser.LoginWebAsync(reader.Subject);

        // Act
        var adminList = await ApiAsync(adminBrowser, HttpMethod.Get, TokensOf(member.UserId));
        var adminOtherTenantList = await ApiAsync(adminBrowser, HttpMethod.Get, TokensOf(otherMember.UserId));
        var adminOtherTenantRevoke = await ApiAsync(adminBrowser, HttpMethod.Post, RevokeOf(otherToken.Token.Id));
        var memberListOfAdmin = await ApiAsync(memberBrowser, HttpMethod.Get, TokensOf(admin.UserId));
        var memberOwnList = await ApiAsync(memberBrowser, HttpMethod.Get, TokensOf(member.UserId));
        var readerList = await ApiAsync(readerBrowser, HttpMethod.Get, TokensOf(member.UserId));
        var readerRevoke = await ApiAsync(readerBrowser, HttpMethod.Post, RevokeOf(memberToken.Token.Id));
        var otherMemberRevoke = await ApiAsync(memberBrowser, HttpMethod.Post, RevokeOf(otherToken.Token.Id));
        var liveBeforeAdminRevoke = (await PatPeople.RowAsync(Host, memberToken.Token.Id)).RevokedAt;
        var adminRevoke = await ApiAsync(adminBrowser, HttpMethod.Post, RevokeOf(memberToken.Token.Id));

        // Assert
        adminList.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await adminList.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        listed.Select(t => t.GetProperty("id").GetString()).ShouldBe([memberToken.Token.Id]);
        listed[0].GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Revoke"]);
        listed[0].EnumerateObject().Select(p => p.Name).ShouldNotContain("value");
        (await adminList.Content.ReadAsStringAsync()).ShouldNotContain(memberToken.Value);
        adminOtherTenantList.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        adminOtherTenantRevoke.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        memberListOfAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        memberOwnList.StatusCode.ShouldBe(HttpStatusCode.OK);
        readerList.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await readerList.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("allowedActions").GetArrayLength().ShouldBe(0);
        readerRevoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        otherMemberRevoke.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        liveBeforeAdminRevoke.ShouldBeNull();
        adminRevoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PatPeople.RowAsync(Host, memberToken.Token.Id)).RevokedReason.ShouldBe(RevokeReason.TokenRevoked);
        (await PatPeople.RowAsync(Host, otherToken.Token.Id)).RevokedAt.ShouldBeNull();
    }

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
    [Property("Rule", "PAT-10")]
    public async Task a_delete_of_the_person_or_of_its_tenant_removes_each_token_of_the_person()
    {
        // Arrange
        var (admin, member) = await PatPeople.AddTenantAsync(Host);
        var memberToken = await PatPeople.StartTokenAsync(Host, member, NoPermissions);
        var adminToken = await PatPeople.StartTokenAsync(Host, admin, NoPermissions, expiresAt: DateTimeOffset.UtcNow.AddYears(1));
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(admin.Subject);
        using var memberClient = PatPeople.ClientWith(Host, memberToken.Value);
        using var adminClient = PatPeople.ClientWith(Host, adminToken.Value);
        Task<int> RowsOf(string userId) => Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == userId));

        // Act
        var userDelete = await ApiAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Users.Delete(member.UserId)}");
        var memberAfter = await memberClient.GetAsync(Tenants);
        var memberRows = await RowsOf(member.UserId);
        var adminBefore = await adminClient.GetAsync(Tenants);
        var tenantDelete = await ApiAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Tenants.Delete(admin.TenantId)}");
        var adminAfter = await adminClient.GetAsync(Tenants);

        // Assert
        userDelete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        memberAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        memberRows.ShouldBe(0);
        adminBefore.StatusCode.ShouldBe(HttpStatusCode.OK);
        tenantDelete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        adminAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RowsOf(admin.UserId)).ShouldBe(0);
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

    private static async Task<HttpResponseMessage> ApiAsync(AuthBrowser browser, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
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
            var user = await db.Users.Include(u => u.Roles).Include(u => u.UserPermissions).SingleAsync(u => u.Id == person.UserId);
            db.Track(held ? user.AddPermission(permission) : user.RemovePermission(permission));
            await db.SaveChangesAsync();
            return true;
        });

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }
}
