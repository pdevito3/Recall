namespace Recall.FunctionalTests.FunctionalTests.Tenants;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Dtos;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using TestUtilities;

/// <summary>
/// The server caches the tenant of each user. These tests check that a change to a user or a tenant
/// shows on the next request, also when an earlier request put the old result in the cache.
/// </summary>
public class TenantLookupCacheTests : TestBase
{
    [Test]
    public async Task user_of_a_deleted_tenant_cannot_read_tenant_data()
    {
        // Arrange
        await StartGraph().AddTenant(out _).BuildAsync();
        await StartGraph()
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Role = "Admin", _ => { })
            .BuildAsync();
        var (owner, member) = (users[0], users[1]);
        SignInAs(member);
        (await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(member.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);

        SignInAs(owner);
        (await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        // Act
        SignInAs(member);
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(member.Id));

        // Assert
        await ShouldHaveNoUserRecordAsync(result);
    }

    [Test]
    public async Task user_of_a_deleted_tenant_with_no_cached_tenant_cannot_read_tenant_data()
    {
        // Arrange
        await StartGraph().AddTenant(out _).BuildAsync();
        await StartGraph()
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Role = "Admin", _ => { })
            .BuildAsync();
        var (owner, member) = (users[0], users[1]);
        SignInAs(owner);
        (await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        // Act
        SignInAs(member);
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(member.Id));

        // Assert
        await ShouldHaveNoUserRecordAsync(result);
    }

    [Test]
    public async Task user_who_starts_a_tenant_can_get_the_tenant_with_a_session_of_their_id()
    {
        // Arrange
        FactoryClient.SignInAsTrustedMachineClient();
        var initiated = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.Initiate, new FakeInitiateUserAndTenantDto().Generate());
        initiated.StatusCode.ShouldBe(HttpStatusCode.Created);
        var started = (await initiated.Content.ReadFromJsonAsync<InitiateUserAndTenantResponseDto>()).ShouldNotBeNull();

        // Act
        FactoryClient.SignInAsAppUser(started.User.Id);
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Tenants.GetRecord(started.Tenant.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task deleted_user_cannot_read_their_tenant()
    {
        // Arrange
        await StartGraph()
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Role = "Admin", _ => { })
            .BuildAsync();
        var (admin, removed) = (users[0], users[1]);
        SignInAs(removed);
        (await FactoryClient.GetRequestAsync(ApiRoutes.Tenants.GetRecord(tenant.Id))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        SignInAs(admin);
        (await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.Delete(removed.Id))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        // Act
        SignInAs(removed);
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Tenants.GetRecord(tenant.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task deleted_user_who_starts_a_tenant_again_resolves_only_as_the_new_user()
    {
        // Arrange
        await StartGraph()
            .AddTenant(out _)
            .AddUsers(out var users, u => u.Role = "Admin", _ => { })
            .BuildAsync();
        var (admin, removed) = (users[0], users[1]);
        SignInAs(removed);
        (await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetByIdentifier(removed.Identifier))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        SignInAs(admin);
        (await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.Delete(removed.Id))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        // Act
        FactoryClient.SignInAsTrustedMachineClient();
        var dto = new FakeInitiateUserAndTenantDto().Generate() with { Role = "Admin" };
        var initiated = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.Initiate, dto);

        // Assert
        initiated.StatusCode.ShouldBe(HttpStatusCode.Created);
        var signedUp = (await initiated.Content.ReadFromJsonAsync<InitiateUserAndTenantResponseDto>()).ShouldNotBeNull();
        signedUp.User.Id.ShouldNotBe(removed.Id);

        SignInAs(removed);
        await ShouldHaveNoUserRecordAsync(await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(signedUp.User.Id)));

        FactoryClient.SignInAsAppUser(signedUp.User.Id);
        var own = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetByIdentifier(signedUp.User.Id));
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ownUser = (await own.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull();
        ownUser.Id.ShouldBe(signedUp.User.Id);

        var update = new UserForUpdateDto
        {
            FirstName = "Again", LastName = ownUser.LastName, Email = ownUser.Email, Username = ownUser.Username,
            Version = ownUser.Version,
        };
        (await FactoryClient.PutJsonRequestAsync(ApiRoutes.Users.Put(ownUser.Id), update)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var actors = await ExecuteDbContextAsync(db => db.AuditEntries
            .Where(e => e.EntityId == ownUser.Id && e.Kind == AuditEntryKind.Updated)
            .Select(e => e.ActorUserId)
            .ToListAsync());
        actors.ShouldBe([signedUp.User.Id]);
    }

    private void SignInAs(User user)
        => FactoryClient.SignInAsAppUser(user.Identifier);

    // The tenant of the user is gone, so the server finds no user record for the caller
    private static async Task ShouldHaveNoUserRecordAsync(HttpResponseMessage result)
    {
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await result.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull().Detail
            .ShouldBe("You are not a member of a tenant. Ask a tenant admin to add you.");
    }
}
