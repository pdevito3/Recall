namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using TestUtilities;

public class JournalAccessTests : TestBase
{
    public static IEnumerable<Func<string, string>> SessionRoutes()
    {
        yield return ApiRoutes.Sessions.GetRecord;
        yield return ApiRoutes.Sessions.RawRevisions;
        yield return ApiRoutes.Sessions.CleanedRevisions;
        yield return id => ApiRoutes.Sessions.RawRevision(id, 1);
        yield return id => ApiRoutes.Sessions.CleanedRevision(id, 1);
    }

    public static IEnumerable<string> RoutesWithNoId()
    {
        yield return ApiRoutes.Sessions.Root;
        yield return ApiRoutes.Sync.Changes;
        yield return ApiRoutes.People.Root;
        yield return ApiRoutes.Corrections.Root;
        yield return ApiRoutes.UserSettings.Root;
    }

    private async Task<SessionDto> SessionOfTheMemberAsync(User member)
    {
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await FactoryClient.SaveDraftAsync(session.Id, "the words of the member", 0);
        await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = "clean words", baseRevision = 0, savedAt = JournalScenario.Morning });
        return session;
    }

    [Test]
    [Property("Rule", "SES-14")]
    [MethodDataSource(nameof(SessionRoutes))]
    public async Task an_admin_of_the_same_tenant_gets_404_for_the_session_of_a_member(Func<string, string> route)
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        var session = await SessionOfTheMemberAsync(member);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var response = await FactoryClient.GetAsync(route(session.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("Rule", "SES-14")]
    public async Task an_admin_cannot_change_or_delete_the_session_of_a_member()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        var session = await SessionOfTheMemberAsync(member);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var save = await FactoryClient.SaveDraftAsync(session.Id, "changed by the admin", 1);
        var delete = await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        FactoryClient.SignInAsAppUser(member.Identifier);
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        save.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        read!.RawText.ShouldBe("the words of the member");
    }

    [Test]
    [Property("Rule", "SES-14")]
    public async Task the_lists_and_the_feed_of_an_admin_do_not_show_a_row_of_a_member()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        var session = await SessionOfTheMemberAsync(member);
        await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "A friend of the member" });
        await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "A term of the member" });
        await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZone = "Europe/Tallinn",
            locationCaptureEnabled = true,
            requirePeopleTagApproval = false,
            savedAt = JournalScenario.Morning,
        });
        await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var sessions = await FactoryClient.GetStringAsync(ApiRoutes.Sessions.Root);
        var people = await FactoryClient.GetStringAsync(ApiRoutes.People.Root);
        var corrections = await FactoryClient.GetStringAsync(ApiRoutes.Corrections.Root);
        var settings = await FactoryClient.GetStringAsync(ApiRoutes.UserSettings.Root);
        var feed = await FactoryClient.PullAsync();

        // Assert
        sessions.ShouldNotContain(session.Id);
        people.ShouldBe("[]");
        corrections.ShouldBe("[]");
        settings.ShouldNotContain("Europe/Tallinn");
        feed.Sessions.ShouldBeEmpty();
        feed.Tombstones.ShouldBeEmpty();
        feed.Settings.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-15")]
    [MethodDataSource(nameof(RoutesWithNoId))]
    public async Task an_all_tenants_client_is_refused_at_a_route_with_no_id(string route)
    {
        // Arrange
        FactoryClient.SignInAsTrustedMachineClient();

        // Act
        var response = await FactoryClient.GetAsync(route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Property("Rule", "SES-15")]
    [MethodDataSource(nameof(SessionRoutes))]
    public async Task an_all_tenants_client_gets_404_for_a_session(Func<string, string> route)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        var session = await SessionOfTheMemberAsync(member);
        FactoryClient.SignInAsMachineClient(TestingWebApplicationFactory.ReadOnlyMachineClientId);

        // Act
        var response = await FactoryClient.GetAsync(route(session.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("Rule", "SES-15")]
    public async Task an_all_tenants_client_cannot_add_to_a_journal()
    {
        // Arrange
        FactoryClient.SignInAsTrustedMachineClient();

        // Act
        var response = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<(User Member, Agent Machine, Agent Delegated)> AddAgentsAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var member, u => u.Role = "User")
            .Under(tenant)
            .AddAgents(out var agents,
                a => { a.Mode = AgentMode.Machine().Value; a.Permissions = Permission.GetAll().Select(p => p.Value).ToList(); },
                a => { a.Mode = AgentMode.Delegated().Value; a.Permissions = Permission.GetAll().Select(p => p.Value).ToList(); });
        await InsertAsync(tenant);
        await InsertAsync(member);
        await InsertAsync(agents);
        return (member, agents[0], agents[1]);
    }

    [Test]
    [Property("Rule", "SES-15")]
    [MethodDataSource(nameof(RoutesWithNoId))]
    public async Task a_machine_agent_with_each_permission_is_refused_at_a_route_with_no_id(string route)
    {
        // Arrange
        var (_, machine, _) = await AddAgentsAsync();
        FactoryClient.SignInAsMachineClient(machine.ClientId);

        // Act
        var response = await FactoryClient.GetAsync(route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Property("Rule", "SES-15")]
    [MethodDataSource(nameof(SessionRoutes))]
    public async Task a_machine_agent_with_each_permission_gets_404_for_a_session_of_its_tenant(Func<string, string> route)
    {
        // Arrange
        var (member, machine, _) = await AddAgentsAsync();
        var session = await SessionOfTheMemberAsync(member);
        FactoryClient.SignInAsMachineClient(machine.ClientId);

        // Act
        var response = await FactoryClient.GetAsync(route(session.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("Rule", "SES-15")]
    public async Task a_delegated_agent_cannot_read_the_journal_of_the_user_it_acts_for()
    {
        // Arrange
        var (member, _, delegated) = await AddAgentsAsync();
        var session = await SessionOfTheMemberAsync(member);
        FactoryClient.SignInAsDelegatedClient(member.Identifier, delegated.ClientId);

        // Act
        var read = await FactoryClient.GetAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var feed = await FactoryClient.GetAsync(ApiRoutes.Sync.Changes);

        // Assert
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        feed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
