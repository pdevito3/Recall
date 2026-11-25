namespace Recall.FunctionalTests.FunctionalTests.Jobs;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Recall.FunctionalTests.Auth;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.PersonalAccessTokens.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Entrypoints;
using Recall.Server.Resources.Entrypoints.Jobs;
using Recall.Server.Resources.Entrypoints.Mcp;
using Shouldly;

// The Owner of AuthHosts.Jobs is the first subject that signs in, so these tests share the lock of JobDashboardTests.
[NotInParallel(nameof(JobDashboardTests))]
public class JobMcpTests
{
    private const string OwnerSubject = "jobs-owner";

    private static readonly string[] BackWaveTools =
    [
        "search_jobs", "count_jobs", "get_job", "get_job_history", "get_job_dependencies", "get_job_payload", "get_job_output",
        "get_observer_lag", "list_observer_dead_letters", "list_workflows", "get_workflow", "cancel_workflow",
        "get_queue_settings", "get_tag_facet", "list_wire_names", "list_schedules", "list_audit_records", "cancel_job",
        "requeue_job", "pause_queue", "resume_queue", "set_concurrency_limit", "trigger_schedule", "get_queue_depths",
    ];

    private static async Task<string> BearerOfAsync(string subject)
    {
        using var browser = new AuthBrowser(AuthHosts.Jobs);
        return (await browser.LoginNativeAsync(subject)).AccessToken;
    }

    private static Task<McpClient> ClientAtAsync(string path, string bearer) => ClientAtAsync(AuthHosts.Jobs, path, bearer);

    private static async Task<McpClient> ClientAtAsync(AuthFlowFactory host, string path, string bearer)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = AuthBrowser.Bearer(bearer);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, path), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
            InitializationTimeout = TimeSpan.FromMinutes(5),
        });
    }

    private static string TextOf(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private const string Tokens = "/api/v1/personal-access-tokens";

    private static readonly TokenGrant JobToolsOnly = new([], JournalAccess.None, jobTools: true);

    private static Task<string> UserIdOfAsync(AuthFlowFactory host, string subject)
        => host.WithDatabaseAsync(db => db.Set<ExternalLogin>().IgnoreQueryFilters()
            .Where(link => link.Subject == subject).Select(link => link.UserId).SingleAsync());

    private static async Task<string> TokenOfAsync(AuthFlowFactory host, string subject, TokenGrant grant)
    {
        var person = new PatPerson(subject, await UserIdOfAsync(host, subject), "");
        return (await PatPeople.StartTokenAsync(host, person, grant)).Value;
    }

    private static async Task<List<string>> JobToolsOfAsync(AuthFlowFactory host, string bearer)
    {
        await using var jobs = await ClientAtAsync(host, JobMcp.Path, bearer);
        return (await jobs.ListToolsAsync()).Select(t => t.Name).ToList();
    }

    private static async Task<HttpResponseMessage> CreateTokenAsync(AuthBrowser browser, bool jobTools)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Tokens)
        {
            Content = JsonContent.Create(new { name = "Claude Code", permissions = Array.Empty<string>(), readJournal = false, writeJournal = false, jobTools, neverExpires = true }),
        };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return await browser.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> MeAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{ApiRoutes.Users.GetCurrent}");
        request.Headers.Add("X-CSRF", "1");
        return await client.SendAsync(request);
    }

    [Test]
    public async Task each_path_has_its_own_server_and_its_own_tools()
    {
        // Arrange
        var owner = await BearerOfAsync(OwnerSubject);

        // Act
        await using var recall = await ClientAtAsync(EntrypointRegistration.McpPath, owner);
        await using var jobs = await ClientAtAsync(JobMcp.Path, owner);
        var recallTools = (await recall.ListToolsAsync()).Select(t => t.Name).ToList();
        var jobTools = (await jobs.ListToolsAsync()).Select(t => t.Name).ToList();
        var depths = await jobs.CallToolAsync("get_queue_depths", new Dictionary<string, object?>());

        // Assert
        recall.ServerInfo.Name.ShouldNotBe("BackWave");
        recallTools.ShouldNotBeEmpty();
        recallTools.ShouldAllBe(name => !BackWaveTools.Contains(name));
        jobs.ServerInfo.Name.ShouldBe("BackWave");
        jobTools.ShouldContain("get_queue_depths");
        jobTools.ShouldAllBe(name => BackWaveTools.Contains(name));
        (depths.IsError ?? false).ShouldBeFalse(TextOf(depths));
        TextOf(depths).ShouldContain("system");
    }

    [Test]
    public void the_tool_access_check_of_the_start_sees_only_the_recall_tools()
    {
        // Arrange
        var tools = AuthHosts.Jobs.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection ?? [];

        // Act
        var check = () => McpToolAccess.Validate(tools);

        // Assert
        check.ShouldNotThrow();
        tools.Select(t => t.ProtocolTool.Name).ShouldAllBe(name => !BackWaveTools.Contains(name));
    }

    [Test]
    public async Task the_owner_can_requeue_and_cannot_do_another_write_or_read_a_payload()
    {
        // Arrange
        await using var jobs = await ClientAtAsync(JobMcp.Path, await BearerOfAsync(OwnerSubject));

        // Act
        var tools = (await jobs.ListToolsAsync()).Select(t => t.Name).ToList();
        var cancel = await jobs.CallToolAsync("cancel_job", new Dictionary<string, object?> { ["jobId"] = Guid.NewGuid().ToString() });

        // Assert
        tools.ShouldContain("requeue_job");
        tools.ShouldNotContain("cancel_job");
        tools.ShouldNotContain("pause_queue");
        tools.ShouldNotContain("resume_queue");
        tools.ShouldNotContain("set_concurrency_limit");
        tools.ShouldNotContain("trigger_schedule");
        tools.ShouldNotContain("cancel_workflow");
        tools.ShouldNotContain("get_job_payload");
        tools.ShouldNotContain("get_job_output");
        (cancel.IsError ?? false).ShouldBeTrue();
    }

    [Test]
    public async Task a_member_who_is_not_the_owner_sees_no_tool_and_runs_none()
    {
        // Arrange
        await BearerOfAsync(OwnerSubject);
        await using var jobs = await ClientAtAsync(JobMcp.Path, await BearerOfAsync($"jobs-member-{Guid.NewGuid():N}"));

        // Act
        var tools = await jobs.ListToolsAsync();
        var depths = await jobs.CallToolAsync("get_queue_depths", new Dictionary<string, object?>());

        // Assert
        tools.ShouldBeEmpty();
        (depths.IsError ?? false).ShouldBeTrue();
        TextOf(depths).ShouldNotContain("system");
    }

    [Test]
    public async Task a_request_with_no_credential_gets_a_401_with_a_bearer_challenge()
    {
        // Arrange
        using var http = AuthHosts.Jobs.CreateClient();

        // Act
        using var answer = await http.PostAsync(JobMcp.Path, ToolsList());

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        answer.Headers.WwwAuthenticate.Select(h => h.Scheme).ShouldContain("Bearer");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task the_session_cookie_of_the_owner_gets_a_401(bool csrf)
    {
        // Arrange
        using var owner = new AuthBrowser(AuthHosts.Jobs);
        await owner.LoginWebAsync(OwnerSubject);
        using var request = new HttpRequestMessage(HttpMethod.Post, JobMcp.Path) { Content = ToolsList() };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (csrf)
            request.Headers.Add("X-CSRF", "1");

        // Act
        using var answer = await owner.SendAsync(request);

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await answer.Content.ReadAsStringAsync()).ShouldNotContain("get_queue_depths");
    }

    [Test]
    public void the_csrf_exception_of_the_dashboard_does_not_reach_the_job_tools()
    {
        // Act
        var dashboardServesTools = JobDashboard.Serves(JobMcp.Path);
        var toolsServeDashboard = JobMcp.Serves(JobDashboard.Path);

        // Assert
        dashboardServesTools.ShouldBeFalse();
        toolsServeDashboard.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "PAT-12")]
    [Property("Rule", "PAT-21")]
    public async Task a_token_of_the_owner_reaches_the_job_tools_only_with_the_job_tools_and_passes_no_other_owner_gate()
    {
        // Arrange
        await BearerOfAsync(OwnerSubject);
        var withJobTools = await TokenOfAsync(AuthHosts.Jobs, OwnerSubject, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite, jobTools: true));
        var withoutJobTools = await TokenOfAsync(AuthHosts.Jobs, OwnerSubject, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite));
        using var http = PatPeople.ClientWith(AuthHosts.Jobs, withJobTools);

        // Act
        await using var jobs = await ClientAtAsync(JobMcp.Path, withJobTools);
        var jobTools = (await jobs.ListToolsAsync()).Select(t => t.Name).ToList();
        var depths = await jobs.CallToolAsync("get_queue_depths", new Dictionary<string, object?>());
        var noJobTools = await JobToolsOfAsync(AuthHosts.Jobs, withoutJobTools);
        await using var recall = await ClientAtAsync(EntrypointRegistration.McpPath, withJobTools);
        var recallTools = (await recall.ListToolsAsync()).Select(t => t.Name).ToList();
        using var dashboard = await http.GetAsync(JobDashboard.Path);
        using var bundle = await http.GetAsync($"/{ApiRoutes.SupportBundle}");

        // Assert
        jobTools.ShouldContain("get_queue_depths");
        jobTools.ShouldContain("requeue_job");
        jobTools.ShouldNotContain("cancel_job");
        (depths.IsError ?? false).ShouldBeFalse(TextOf(depths));
        TextOf(depths).ShouldContain("system");
        noJobTools.ShouldBeEmpty();
        recallTools.ShouldNotBeEmpty();
        recallTools.ShouldAllBe(name => !BackWaveTools.Contains(name));
        dashboard.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        bundle.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Property("Rule", "PAT-21")]
    public async Task a_token_with_the_job_tools_of_a_person_who_is_not_the_owner_sees_no_tool_and_runs_none()
    {
        // Arrange
        await BearerOfAsync(OwnerSubject);
        var member = $"jobs-member-{Guid.NewGuid():N}";
        await BearerOfAsync(member);
        var token = await TokenOfAsync(AuthHosts.Jobs, member, JobToolsOnly);
        await using var jobs = await ClientAtAsync(JobMcp.Path, token);

        // Act
        var tools = await jobs.ListToolsAsync();
        var depths = await jobs.CallToolAsync("get_queue_depths", new Dictionary<string, object?>());

        // Assert
        tools.ShouldBeEmpty();
        (depths.IsError ?? false).ShouldBeTrue();
        TextOf(depths).ShouldNotContain("system");
    }

    [Test]
    [Property("Rule", "PAT-19")]
    [Property("Rule", "PAT-21")]
    public async Task only_the_owner_makes_a_token_with_the_job_tools_and_the_audit_action_holds_them()
    {
        // Arrange
        using var owner = new AuthBrowser(AuthHosts.Jobs);
        await owner.LoginWebAsync(OwnerSubject);
        using var member = new AuthBrowser(AuthHosts.Jobs);
        var memberSubject = $"jobs-member-{Guid.NewGuid():N}";
        await member.LoginWebAsync(memberSubject);
        var memberId = await UserIdOfAsync(AuthHosts.Jobs, memberSubject);

        // Act
        using var byOwner = await CreateTokenAsync(owner, jobTools: true);
        using var byMember = await CreateTokenAsync(member, jobTools: true);
        using var byMemberWithout = await CreateTokenAsync(member, jobTools: false);

        // Assert
        byOwner.StatusCode.ShouldBe(HttpStatusCode.Created, await byOwner.Content.ReadAsStringAsync());
        var created = await byOwner.Content.ReadFromJsonAsync<JsonElement>();
        var tokenId = created.GetProperty("token").GetProperty("id").GetString()!;
        created.GetProperty("token").GetProperty("jobTools").GetBoolean().ShouldBeTrue();
        (await JobToolsOfAsync(AuthHosts.Jobs, created.GetProperty("value").GetString()!)).ShouldContain("get_queue_depths");
        byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var problem = JsonDocument.Parse(await byMember.Content.ReadAsStringAsync()))
            problem.RootElement.GetProperty("detail").GetString().ShouldBe(AddPersonalAccessToken.OnlyTheOwnerGivesJobTools);
        byMemberWithout.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await byMemberWithout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetProperty("jobTools").GetBoolean().ShouldBeFalse();
        var tokensOfMember = await AuthHosts.Jobs.WithDatabaseAsync(db => db.Set<AuthSession>().IgnoreQueryFilters()
            .CountAsync(s => s.UserId == memberId && s.ClientKind == SessionKind.Pat));
        tokensOfMember.ShouldBe(1);
        var grant = await AuthHosts.Jobs.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Kind == AuditEntryKind.Action && e.Action == "access_token_created" && e.Data.Contains(tokenId))
            .Select(e => e.Data).SingleAsync());
        JsonDocument.Parse(grant).RootElement.GetProperty("Grant").GetString().ShouldBe(JobToolsOnly.Encode());
    }

    [Test]
    public async Task me_says_is_owner_only_to_the_owner_in_person()
    {
        // Arrange
        var owner = await BearerOfAsync(OwnerSubject);
        var member = await BearerOfAsync($"jobs-member-{Guid.NewGuid():N}");
        var ownerToken = await TokenOfAsync(AuthHosts.Jobs, OwnerSubject, new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite, jobTools: true));
        using var ownerClient = AuthHosts.Jobs.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = AuthBrowser.Bearer(owner);
        using var memberClient = AuthHosts.Jobs.CreateClient();
        memberClient.DefaultRequestHeaders.Authorization = AuthBrowser.Bearer(member);
        using var tokenClient = PatPeople.ClientWith(AuthHosts.Jobs, ownerToken);

        // Act
        using var ownerMe = await MeAsync(ownerClient);
        using var memberMe = await MeAsync(memberClient);
        using var tokenMe = await MeAsync(tokenClient);

        // Assert
        (await ownerMe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isOwner").GetBoolean().ShouldBeTrue();
        (await memberMe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isOwner").GetBoolean().ShouldBeFalse();
        (await tokenMe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isOwner").GetBoolean().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "PAT-21")]
    public async Task a_token_with_the_job_tools_loses_them_on_the_next_request_after_its_person_stops_being_the_owner()
    {
        // Arrange
        var host = AuthHosts.JobToolsDemotion;
        using var owner = new AuthBrowser(host);
        await owner.LoginWebAsync("tools-owner");
        using var next = new AuthBrowser(host);
        await next.LoginWebAsync("tools-next-admin");
        var token = await TokenOfAsync(host, "tools-owner", JobToolsOnly);
        var nextToken = await TokenOfAsync(host, "tools-next-admin", JobToolsOnly);
        (await SendRoleAsync(owner, await UserIdOfAsync(host, "tools-next-admin"), "Admin")).IsSuccessStatusCode.ShouldBeTrue();
        var before = await JobToolsOfAsync(host, token);

        // Act
        using var demoted = await SendRoleAsync(next, await UserIdOfAsync(host, "tools-owner"), "User");
        var after = await JobToolsOfAsync(host, token);
        var ofTheNextOwner = await JobToolsOfAsync(host, nextToken);

        // Assert
        demoted.IsSuccessStatusCode.ShouldBeTrue(await demoted.Content.ReadAsStringAsync());
        before.ShouldContain("get_queue_depths");
        after.ShouldBeEmpty();
        ofTheNextOwner.ShouldContain("get_queue_depths");
    }

    private static async Task<HttpResponseMessage> SendRoleAsync(AuthBrowser browser, string userId, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/{ApiRoutes.Users.UpdateRole(userId)}") { Content = JsonContent.Create(new { role }) };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return await browser.SendAsync(request);
    }

    private static StringContent ToolsList()
        => new("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json");
}
