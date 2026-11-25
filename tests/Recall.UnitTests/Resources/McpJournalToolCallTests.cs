namespace Recall.UnitTests.Resources;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Recall.Server.Databases;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints.Mcp;
using Shouldly;

public class McpJournalToolCallTests
{
    private static readonly LiveUser Person = new("usr_1", "ten_1");

    [Test]
    [Property("Rule", "IMP-22")]
    [Arguments(ToolAccessKind.JournalRead)]
    [Arguments(ToolAccessKind.JournalWrite)]
    public async Task a_caller_with_no_journal_is_refused_before_the_tool_runs(ToolAccessKind kind)
    {
        // Act
        var (call, ran) = Call(kind, new CallerIdentity.MachineAgent("agt_1", "client", "ten_1"));

        // Assert
        (await call.ShouldThrowAsync<ForbiddenAccessException>()).Message.ShouldBe(JournalOwnerExtensions.NoJournalMessage);
        ran().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-22")]
    public async Task a_token_with_read_journal_access_is_refused_a_write_tool_before_it_runs()
    {
        // Act
        var (call, ran) = Call(ToolAccessKind.JournalWrite, TokenWith(JournalAccess.Read));

        // Assert
        (await call.ShouldThrowAsync<ForbiddenAccessException>()).Message.ShouldBe(JournalWriteRule.Refusal);
        ran().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-22")]
    [Arguments(ToolAccessKind.JournalRead, "Read")]
    [Arguments(ToolAccessKind.JournalRead, "ReadWrite")]
    [Arguments(ToolAccessKind.JournalWrite, "ReadWrite")]
    public async Task a_token_with_enough_journal_access_reaches_the_tool(ToolAccessKind kind, string access)
    {
        // Act
        var (call, ran) = Call(kind, TokenWith(access == "Read" ? JournalAccess.Read : JournalAccess.ReadWrite));
        await call();

        // Assert
        ran().ShouldBeTrue();
    }

    private static CallerIdentity.PersonalAccessToken TokenWith(JournalAccess journal)
        => new("pat_1", "user-1", Person, new TokenGrant(Permission.GetAll(), journal));

    private static (Func<Task<CallToolResult>> Call, Func<bool> Ran) Call(ToolAccessKind kind, CallerIdentity identity)
    {
        var ran = false;
        var resolver = Substitute.For<ICallerIdentityResolver>();
        resolver.GetAsync(Arg.Any<CancellationToken>()).Returns(identity);
        var services = new ServiceCollection()
            .AddSingleton(resolver)
            .AddSingleton(Substitute.For<IPermissionGuard>())
            .BuildServiceProvider();
        var handler = McpToolAccess.CallFilter((_, _) =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = new RequestContext<CallToolRequestParams>(
            Substitute.For<McpServer>(),
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "journal_tool" })
        {
            Services = services,
            MatchedPrimitive = ToolOf(kind),
        };
        return (async () => await handler(request, CancellationToken.None), () => ran);
    }

    private static McpServerTool ToolOf(ToolAccessKind kind) => kind == ToolAccessKind.JournalRead
        ? McpServerTool.Create([ToolAccess(ToolAccessKind.JournalRead)] () => "pong", new McpServerToolCreateOptions { Name = "journal_tool" })
        : McpServerTool.Create([ToolAccess(ToolAccessKind.JournalWrite)] () => "pong", new McpServerToolCreateOptions { Name = "journal_tool" });
}
