namespace Recall.FunctionalTests.FunctionalTests.Users;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using TestUtilities;

/// <summary>A delegated agent acts for a person, but a link names a way in of that person, so the agent gets 403 on each link route.</summary>
public class ExternalLoginAgentTests : TestBase
{
    // IDP-19 also covers the login-settings routes of a later PR, so this test does not name it yet.
    [Test]
    public async Task a_delegated_agent_with_each_permission_gets_403_on_each_link_route()
    {
        // Arrange
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = "Admin")
            .Under(tenant)
            .AddAgents(out var agents, a =>
            {
                a.Mode = AgentMode.Delegated().Value;
                a.Permissions = Permission.GetAll().Where(p => p != Permission.ManageModelProviders()).Select(p => p.Value).ToList();
            });
        await InsertAsync(tenant);
        await InsertAsync(admin);
        await InsertAsync(agents);
        FactoryClient.SignInAsDelegatedClient(admin.Identifier, agents[0].ClientId);
        var body = new { issuer = "https://idp.test", subject = "someone" };

        // Act
        HttpResponseMessage[] responses =
        [
            await FactoryClient.GetAsync($"{ApiRoutes.Base}/me/external-logins"),
            await FactoryClient.GetAsync($"{ApiRoutes.Base}/users/{admin.Id}/external-logins"),
            await FactoryClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{ApiRoutes.Base}/me/external-logins") { Content = JsonContent.Create(body) }),
            await FactoryClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{ApiRoutes.Base}/users/{admin.Id}/external-logins") { Content = JsonContent.Create(body) }),
            await FactoryClient.PostAsync("/auth/oidc/any/link", null),
        ];

        // Assert
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Forbidden);
    }
}
