namespace Recall.FunctionalTests.FunctionalTests.ImportBatches;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recall.FunctionalTests.Auth;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using TestUtilities;

public class ImportBatchMcpTests : TestBase
{
    private const string Day = "2024-03-01";

    private static readonly string[] ListTools = ["list_import_batches", "list_import_entries", "list_journal_days"];

    private static readonly string[] ImportTools =
    [
        .. ListTools, "open_import_batch", "put_import_entries", "remove_import_entries", "accept_import_batch", "discard_import_batch",
    ];

    private static readonly string[] OneId = ["a"];

    private static AuthFlowFactory Host => AuthHosts.Open;

    public static IEnumerable<Func<(string Journal, string[] Tools)>> ToolsOfEachToken()
    {
        yield return () => ("read_write", ImportTools);
        yield return () => ("read", ListTools);
        yield return () => ("none", []);
    }

    [Test]
    [Property("Rule", "IMP-22")]
    [MethodDataSource(nameof(ToolsOfEachToken))]
    public async Task tools_list_shows_a_token_only_the_import_tools_of_its_journal_access(string journal, string[] expected)
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([], JournalAccess.FromName(journal)));
        using var client = PatPeople.ClientWith(Host, token.Value);
        await using var mcp = await PatPeople.McpAsync(client);

        // Act
        var tools = await mcp.ListToolsAsync();

        // Assert
        tools.Select(t => t.Name).Where(ImportTools.Contains).ShouldBe(expected, ignoreOrder: true);
    }

    public static IEnumerable<string> CallersWithNoJournal()
    {
        yield return "machine agent with each permission";
        yield return "delegated agent with each permission";
        yield return "all-tenants client that manages";
        yield return "all-tenants client that reads";
    }

    [Test]
    [Property("Rule", "IMP-22")]
    [Property("Rule", "SES-15")]
    [MethodDataSource(nameof(CallersWithNoJournal))]
    public async Task tools_list_shows_a_caller_with_no_journal_no_import_tool_and_a_call_is_refused(string caller)
    {
        // Arrange
        await SignInWithNoJournalAsync(caller);
        await using var mcp = await CreateMcpClientAsync();

        // Act
        var tools = await mcp.ListToolsAsync();
        var call = await mcp.CallToolAsync("list_import_batches", new Dictionary<string, object?>());

        // Assert
        tools.Select(t => t.Name).Where(ImportTools.Contains).ShouldBeEmpty();
        call.ShouldBeToolError("permission_denied");
        DetailOf(call).ShouldBe("Only the person who owns a journal can use it.");
    }

    [Test]
    [Property("Rule", "IMP-22")]
    [Property("Rule", "IMP-23")]
    public async Task a_token_that_only_reads_the_journal_is_refused_each_write_tool_and_changes_nothing()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var writer = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([], JournalAccess.ReadWrite));
        var reader = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([], JournalAccess.Read));
        using var writerClient = PatPeople.ClientWith(Host, writer.Value);
        await using var writerMcp = await PatPeople.McpAsync(writerClient);
        var batchId = (await CallAsync(writerMcp, "open_import_batch", new() { ["name"] = "Diary", ["timeZone"] = "UTC" }))
            .GetProperty("id").GetString()!;
        using var readerClient = PatPeople.ClientWith(Host, reader.Value);
        await using var readerMcp = await PatPeople.McpAsync(readerClient);

        // Act
        var refused = new List<CallToolResult>
        {
            await readerMcp.CallToolAsync("open_import_batch", new Dictionary<string, object?> { ["name"] = "Other", ["timeZone"] = "UTC" }),
            await readerMcp.CallToolAsync("put_import_entries", new Dictionary<string, object?> { ["id"] = batchId, ["entries"] = Entries("a") }),
            await readerMcp.CallToolAsync("remove_import_entries", new Dictionary<string, object?> { ["id"] = batchId, ["clientSessionIds"] = OneId }),
            await readerMcp.CallToolAsync("accept_import_batch", new Dictionary<string, object?> { ["id"] = batchId, ["version"] = 0 }),
            await readerMcp.CallToolAsync("discard_import_batch", new Dictionary<string, object?> { ["id"] = batchId }),
        };
        var batches = await CallAsync(readerMcp, "list_import_batches", []);

        // Assert
        foreach (var result in refused)
        {
            result.ShouldBeToolError("permission_denied");
            DetailOf(result).ShouldBe("This personal access token can read your journal but cannot change it.");
        }

        var batch = batches.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        batch.GetProperty("id").GetString().ShouldBe(batchId);
        batch.GetProperty("entryCount").GetInt32().ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IMP-22")]
    [Property("Rule", "IMP-18")]
    [Property("Rule", "IMP-3")]
    [Property("Rule", "IMP-7")]
    [Property("Rule", "IMP-21")]
    public async Task an_agent_with_a_token_imports_over_mcp_and_only_the_person_accepts_with_the_review_on()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        var token = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([], JournalAccess.ReadWrite));
        using var client = PatPeople.ClientWith(Host, token.Value);
        await using var mcp = await PatPeople.McpAsync(client);

        // Act
        var batch = await CallAsync(mcp, "open_import_batch", new() { ["name"] = "Day One", ["timeZone"] = "UTC" });
        var batchId = batch.GetProperty("id").GetString()!;
        var put = await CallAsync(mcp, "put_import_entries", new() { ["id"] = batchId, ["entries"] = Entries("first", "second") });
        var again = await CallAsync(mcp, "put_import_entries", new() { ["id"] = batchId, ["entries"] = Entries("first", "second") });
        var entries = await CallAsync(mcp, "list_import_entries", new() { ["id"] = batchId });
        var days = await CallAsync(mcp, "list_journal_days", new() { ["from"] = "2024-01-01", ["to"] = "2024-12-31" });
        var accept = await mcp.CallToolAsync("accept_import_batch", new Dictionary<string, object?>
        {
            ["id"] = batchId,
            ["version"] = entries.GetProperty("version").GetUInt32(),
        });
        var discard = await mcp.CallToolAsync("discard_import_batch", new Dictionary<string, object?> { ["id"] = batchId });
        var after = await CallAsync(mcp, "list_import_batches", []);

        // Assert
        batch.GetProperty("state").GetString().ShouldBe("Open");
        OutcomesOf(put).ShouldBe([("first", "Added"), ("second", "Added")]);
        OutcomesOf(again).ShouldBe([("first", "Unchanged"), ("second", "Unchanged")]);
        again.GetProperty("version").GetUInt32().ShouldBe(put.GetProperty("version").GetUInt32());
        entries.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("clientSessionId").GetString(), e.GetProperty("raw").GetString(), e.GetProperty("startsAt").GetDateTimeOffset()))
            .ShouldBe([
                ("first", "Words of **first**", new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero)),
                ("second", "Words of **second**", new DateTimeOffset(2024, 3, 1, 12, 1, 0, TimeSpan.Zero)),
            ]);
        days.GetProperty("items").EnumerateArray().ShouldBeEmpty();
        accept.ShouldBeToolError("permission_denied");
        DetailOf(accept).ShouldBe(AcceptImportBatch.ReviewMessage);
        discard.IsError.ShouldNotBe(true);
        after.GetProperty("items").EnumerateArray().ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "IMP-18")]
    [Property("Rule", "IMP-26")]
    public async Task an_agent_with_a_token_accepts_over_mcp_when_the_person_turned_the_review_off()
    {
        // Arrange
        var (_, member) = await PatPeople.AddTenantAsync(Host);
        await TurnTheReviewOffAsync(member);
        var token = await PatPeople.StartTokenAsync(Host, member, new TokenGrant([], JournalAccess.ReadWrite));
        using var client = PatPeople.ClientWith(Host, token.Value);
        await using var mcp = await PatPeople.McpAsync(client);
        var batchId = (await CallAsync(mcp, "open_import_batch", new() { ["name"] = "Day One", ["timeZone"] = "UTC" }))
            .GetProperty("id").GetString()!;
        var version = (await CallAsync(mcp, "put_import_entries", new() { ["id"] = batchId, ["entries"] = Entries("first", "second") }))
            .GetProperty("version").GetUInt32();

        // Act
        var accepted = await CallAsync(mcp, "accept_import_batch", new() { ["id"] = batchId, ["version"] = version });
        var days = await CallAsync(mcp, "list_journal_days", new() { ["from"] = "2024-01-01", ["to"] = "2024-12-31" });

        // Assert
        accepted.GetProperty("state").GetString().ShouldBe("Accepted");
        accepted.GetProperty("result").GetProperty("sessionsMade").GetInt32().ShouldBe(2);
        days.GetProperty("items").EnumerateArray()
            .Select(d => (d.GetProperty("date").GetString(), d.GetProperty("sessionCount").GetInt32()))
            .ShouldBe([(Day, 2)]);
    }

    [Test]
    [NotInParallel(nameof(AuthHosts.KeepAlive))]
    [Property("Rule", "IMP-28")]
    public async Task a_tool_call_whose_body_is_more_than_16_mb_gets_413_on_kestrel_and_stores_nothing()
    {
        // Arrange
        var host = AuthHosts.KeepAlive;
        var (_, member) = await PatPeople.AddTenantAsync(host);
        var token = await PatPeople.StartTokenAsync(host, member, new TokenGrant([], JournalAccess.ReadWrite));
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        // Kestrel refuses the body before it reads it, so a client that does not wait for 100 Continue sees a broken pipe
        client.DefaultRequestHeaders.ExpectContinue = true;
        await using var mcp = await PatPeople.McpAsync(client);
        var batchId = (await CallAsync(mcp, "open_import_batch", new() { ["name"] = "Large", ["timeZone"] = "UTC" }))
            .GetProperty("id").GetString()!;
        var raw = new string('a', 900_000);
        var entries = Enumerable.Range(0, 19).Select(i => new { clientSessionId = $"large-{i}", date = Day, position = i, raw });
        var call = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "put_import_entries", arguments = new { id = batchId, entries } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, EntrypointRegistration.McpPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull();
        problem.Detail.ShouldBe(PutImportEntriesEndpoint.TooLargeMessage);
        (await CallAsync(mcp, "list_import_entries", new() { ["id"] = batchId })).GetProperty("entries").EnumerateArray().ShouldBeEmpty();
    }

    private static object[] Entries(params string[] ids)
        => [.. ids.Select((id, position) => new { clientSessionId = id, date = Day, position, raw = $"Words of **{id}**" })];

    private static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        var result = await mcp.CallToolAsync(tool, arguments);
        result.IsError.ShouldNotBe(true, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        return result.StructuredContent.ShouldNotBeNull();
    }

    private static List<(string?, string?)> OutcomesOf(JsonElement put)
        => [.. put.GetProperty("outcomes").EnumerateArray().Select(o => (o.GetProperty("clientSessionId").GetString(), o.GetProperty("outcome").GetString()))];

    private static string? DetailOf(CallToolResult result)
        => result.StructuredContent.ShouldNotBeNull().GetProperty("detail").GetString();

    private static async Task TurnTheReviewOffAsync(PatPerson person)
    {
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(person.Subject);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/{ApiRoutes.UserSettings.ImportReview}")
        {
            Content = JsonContent.Create(new { requireImportReview = false, version = 0 }),
        };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(Recall.Server.Resources.Entrypoints.Auth.PageUserCheck.Header, await browser.UserOfSessionAsync());
        var response = await browser.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task SignInWithNoJournalAsync(string caller)
    {
        switch (caller)
        {
            case "machine agent with each permission":
            case "delegated agent with each permission":
                TestGraph.Start(out _)
                    .AddTenant(out var tenant)
                    .AddUser(out var member, u => u.Role = "User")
                    .Under(tenant)
                    .AddAgents(out var agents,
                        a => { a.Mode = AgentMode.Machine().Value; a.Permissions = EachPermissionOfAnAgent(); },
                        a => { a.Mode = AgentMode.Delegated().Value; a.Permissions = EachPermissionOfAnAgent(); });
                await InsertAsync(tenant);
                await InsertAsync(member);
                await InsertAsync(agents);
                if (caller.StartsWith("machine", StringComparison.Ordinal))
                    FactoryClient.SignInAsMachineClient(agents[0].ClientId);
                else
                    FactoryClient.SignInAsDelegatedClient(member.Identifier, agents[1].ClientId);
                break;
            case "all-tenants client that manages":
                FactoryClient.SignInAsTrustedMachineClient();
                break;
            case "all-tenants client that reads":
                FactoryClient.SignInAsMachineClient(TestingWebApplicationFactory.ReadOnlyMachineClientId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caller), caller, null);
        }
    }

    private static List<string> EachPermissionOfAnAgent()
        => Permission.GetAll().Where(p => p != Permission.ManageModelProviders()).Select(p => p.Value).ToList();
}
