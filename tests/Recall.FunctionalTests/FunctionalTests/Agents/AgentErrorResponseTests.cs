namespace Recall.FunctionalTests.FunctionalTests.Agents;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.Agents.Dtos;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using TestUtilities;

public class AgentErrorResponseTests : TestBase
{
    [Test]
    public async Task invalid_permission_of_the_first_grant_is_an_error_under_its_item()
    {
        // Arrange
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = "Admin")
            .Under(tenant);
        await InsertAsync(tenant);
        await InsertAsync(admin);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Agents.Create, new AgentForCreationDto
        {
            Name = "Clinic assistant",
            ClientId = $"clinic-assistant-{Guid.NewGuid():N}",
            Mode = AgentMode.DelegatedValue,
            Permissions = [Permission.Names.ReadUsers, "nope"],
        });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = (await result.Content.ReadFromJsonAsync<ValidationProblemDetails>()).ShouldNotBeNull();
        var error = problem.Errors.ShouldHaveSingleItem();
        error.Key.ShouldBe("permissions[1]");
        error.Value.ShouldBe(["Invalid permission: nope"]);
    }

    [Test]
    [Property("Rule", "PAT-15")]
    public async Task an_agent_whose_client_id_is_a_personal_access_token_id_is_refused()
    {
        // Arrange
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = "Admin")
            .Under(tenant);
        await InsertAsync(tenant);
        await InsertAsync(admin);
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var clientId = $"pat_{Guid.NewGuid():N}";

        // Act
        var result = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Agents.Create, new AgentForCreationDto
        {
            Name = "Looks like a token",
            ClientId = clientId,
            Mode = AgentMode.DelegatedValue,
            Permissions = [],
        });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = (await result.Content.ReadFromJsonAsync<ValidationProblemDetails>()).ShouldNotBeNull();
        problem.Errors.ShouldHaveSingleItem().Key.ShouldBe("clientId");
        (await ExecuteDbContextAsync(db => db.Agents.IgnoreQueryFilters().AnyAsync(a => a.ClientId == clientId))).ShouldBeFalse();
    }
}
