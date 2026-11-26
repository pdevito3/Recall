namespace Recall.FunctionalTests.FunctionalTests.Users;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Dtos;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Shouldly;
using TestUtilities;

/// <summary>
/// The user record says which actions the caller can send for it now, so the SPA shows a button without a copy of
/// the permission rules.
/// </summary>
public class UserAllowedActionsTests : TestBase
{
    [Test]
    public async Task admin_gets_each_action_for_another_user()
    {
        // Arrange
        var (admin, other) = await AddTwoUsersAsync("Admin", "Member");
        SignInAs(admin);

        // Act
        var allowedActions = await GetAllowedActionsAsync(other.Id);

        // Assert
        allowedActions.ShouldBe(["Update", "AddRole", "RemoveRole", "AddPermission", "RemovePermission", "Delete", "IssueTemporaryPassword", "ManageExternalLogins"]);
    }

    [Test]
    public async Task reader_with_no_other_permission_gets_no_action_for_another_user()
    {
        // Arrange
        var (reader, other) = await AddTwoUsersAsync("Member", "Member",
            first => first.AddPermission(Permission.ReadUsers()));
        SignInAs(reader);

        // Act
        var allowedActions = await GetAllowedActionsAsync(other.Id);

        // Assert
        allowedActions.ShouldBeEmpty();
    }

    [Test]
    public async Task member_with_no_permission_can_only_update_their_own_record()
    {
        // Arrange
        var (member, _) = await AddTwoUsersAsync("Member", "Member");
        SignInAs(member);

        // Act
        var allowedActions = await GetAllowedActionsAsync(member.Id);

        // Assert
        allowedActions.ShouldBe(["Update", "ManageExternalLogins"]);
    }

    [Test]
    [Arguments("Admin", new[] { "Admin", "UserManager" })]
    [Arguments("UserManager", new[] { "UserManager" })]
    public async Task addable_roles_are_the_roles_that_the_caller_can_give_and_the_auditor_lacks(string callerRole, string[] expected)
    {
        // Arrange
        var (caller, auditor) = await AddTwoUsersAsync(callerRole, "Auditor");
        SignInAs(caller);

        // Act
        var detail = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(auditor.Id));
        var list = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetList);

        // Assert
        (await detail.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull().AddableRoles.ShouldBe(expected);
        (await list.Content.ReadFromJsonAsync<List<UserDto>>()).ShouldNotBeNull()
            .Single(u => u.Id == auditor.Id).AddableRoles.ShouldBe(expected);
    }

    [Test]
    public async Task a_caller_who_cannot_add_a_role_gets_no_addable_role()
    {
        // Arrange
        var (reader, other) = await AddTwoUsersAsync("Auditor", "Member");
        SignInAs(reader);

        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(other.Id));

        // Assert
        (await result.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull().AddableRoles.ShouldBeEmpty();
    }

    [Test]
    public async Task each_row_of_the_list_has_the_actions_of_the_caller()
    {
        // Arrange
        var (manager, admin) = await AddTwoUsersAsync("Member", "Admin",
            first => first.AddPermissions([Permission.ManageUsers(), Permission.ReadUsers()]));
        SignInAs(manager);

        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetList);

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var users = (await result.Content.ReadFromJsonAsync<List<UserDto>>()).ShouldNotBeNull();
        // The manager cannot take away the permissions of the admin, so the admin row has no Delete
        users.Single(u => u.Id == admin.Id).AllowedActions.ShouldBe(["Update"]);
        users.Single(u => u.Id == manager.Id).AllowedActions.ShouldBe(["Update", "Delete", "ManageExternalLogins"]);
    }

    [Test]
    public async Task update_response_has_the_actions_for_the_permissions_of_the_user()
    {
        // Arrange
        var (manager, admin) = await AddTwoUsersAsync("Member", "Admin",
            first => first.AddPermissions([Permission.ManageUsers(), Permission.ReadUsers()]));
        SignInAs(manager);
        var dto = new FakeUserForUpdateDto().Generate() with { Version = admin.Version };

        // Act
        var result = await FactoryClient.PutJsonRequestAsync(ApiRoutes.Users.Put(admin.Id), dto);

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = (await result.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull();
        user.AllowedActions.ShouldBe(["Update"]);
    }

    [Test]
    public async Task admin_who_removes_their_own_role_admin_gets_the_actions_of_a_member()
    {
        // Arrange
        var (admin, _) = await AddTwoUsersAsync("Admin", "Admin");
        SignInAs(admin);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.RemoveRole(admin.Id, "Admin"));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = (await result.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull();
        user.AllowedActions.ShouldBe(["Update", "ManageExternalLogins"]);
    }

    [Test]
    public async Task a_user_who_removes_their_own_role_auditor_cannot_add_it_back()
    {
        // Arrange
        var (manager, _) = await AddTwoUsersAsync("UserManager", "Member", first => first.AddRole(UserRole.Auditor()));
        SignInAs(manager);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.RemoveRole(manager.Id, "Auditor"));
        var addBack = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.AddRole(manager.Id), new { role = "Auditor" });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = (await result.Content.ReadFromJsonAsync<UserDto>()).ShouldNotBeNull();
        user.AllowedActions.ShouldContain("AddRole");
        user.AddableRoles.ShouldBeEmpty();
        addBack.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<string[]> GetAllowedActionsAsync(string userId)
    {
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(userId));
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("allowedActions", out var allowedActions)
            .ShouldBeTrue("The user record has no allowedActions.");
        return allowedActions.EnumerateArray().Select(a => a.GetString()!).ToArray();
    }

    private void SignInAs(User user)
        => FactoryClient.SignInAsAppUser(user.Identifier);

    private static async Task<(User First, User Second)> AddTwoUsersAsync(
        string firstRole, string secondRole, Action<User>? changeFirst = null)
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Roles = RolesOf(firstRole), u => u.Roles = RolesOf(secondRole));
        changeFirst?.Invoke(users[0]);
        await InsertAsync(tenant);
        foreach (var user in users)
            await InsertAsync(user);
        return (users[0], users[1]);
    }

    private static IReadOnlyList<string> RolesOf(string role) => role == "Member" ? [] : [role];
}
