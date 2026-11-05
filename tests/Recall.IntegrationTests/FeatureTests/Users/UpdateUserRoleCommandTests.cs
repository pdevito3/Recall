namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.Server.Resources;
using Recall.IntegrationTests.Databases;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class UpdateUserRoleCommandTests : TestBase
{
    [Test]
    public async Task member_cannot_change_a_role()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var member, u => u.Role = "User")
            .BuildAsync();
        testingServiceScope.SetUserWithDefaults(identifier: member.Identifier);

        // Act
        var act = () => testingServiceScope.SendAsync(new UpdateUserRole.Command(member.Id, "Admin"));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
        var role = await testingServiceScope.ExecuteDbContextAsync(db => db.Users
            .Where(u => u.Id == member.Id).Select(u => u.Role.Value).SingleAsync());
        role.ShouldBe("User");
    }

    [Test]
    public async Task can_change_role_from_user_to_admin()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "User")
            .BuildAsync();

        // Act
        var command = new UpdateUserRole.Command(createdUser.Id, "Admin");
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        userReturned.Role.ShouldBe("Admin");
        userReturned.Permissions.ShouldContain("manage_tenant");
    }

    [Test]
    public async Task changing_role_to_admin_grants_admin_permissions_in_db()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "User")
            .BuildAsync();

        // Act
        var command = new UpdateUserRole.Command(createdUser.Id, "Admin");
        await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users
                .Include(u => u.UserPermissions)
                .FirstOrDefaultAsync(u => u.Id == createdUser.Id));

        userInDb.ShouldNotBeNull();
        userInDb.Role.Value.ShouldBe("Admin");
        userInDb.HasPermission(Permission.ManageTenant()).ShouldBeTrue();
    }

    [Test]
    public async Task can_change_role_from_admin_to_user()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "Admin")
            .BuildAsync();

        // Act
        var command = new UpdateUserRole.Command(createdUser.Id, "User");
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        userReturned.Role.ShouldBe("User");
        userReturned.Permissions.ShouldBeEmpty();
    }

    [Test]
    public async Task changing_role_from_admin_to_user_removes_permissions()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "Admin")
            .BuildAsync();

        // Verify admin has permissions before role change
        createdUser.UserPermissions.ShouldNotBeEmpty();

        // Act
        var command = new UpdateUserRole.Command(createdUser.Id, "User");
        await testingServiceScope.SendAsync(command);

        // Assert
        var userAfter = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users
                .Include(u => u.UserPermissions)
                .FirstOrDefaultAsync(u => u.Id == createdUser.Id));

        userAfter.ShouldNotBeNull();
        userAfter.Role.Value.ShouldBe("User");
        userAfter.UserPermissions.ShouldBeEmpty();
    }

    [Test]
    public async Task changing_role_resets_custom_permissions_to_role_defaults()
    {
        // Arrange - Create admin
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "Admin")
            .BuildAsync();

        // Act - Change to admin again (simulating permission reset)
        var command = new UpdateUserRole.Command(createdUser.Id, "Admin");
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert - Should have exactly the default admin permissions
        userReturned.Permissions.ShouldBe(Permission.ListNames(), ignoreOrder: true);
    }

    [Test]
    public async Task throws_when_user_not_found()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var nonExistentId = EntityId.New(User.IdPrefix);

        // Act & Assert
        var command = new UpdateUserRole.Command(nonExistentId, "Admin");
        await Should.ThrowAsync<Exception>(async () =>
            await testingServiceScope.SendAsync(command));
    }

    [Test]
    public async Task throws_when_invalid_role()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser)
            .BuildAsync();

        // Act & Assert
        var command = new UpdateUserRole.Command(createdUser.Id, "InvalidRole");
        await Should.ThrowAsync<ValidationException>(async () =>
            await testingServiceScope.SendAsync(command));
    }

    [Test]
    public async Task setting_the_same_role_keeps_the_permissions()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Role = "Admin")
            .BuildAsync();
        await testingServiceScope.SendAsync(new RemoveUserPermission.Command(createdUser.Id, "manage_tenant"));

        // Act
        var userReturned = await testingServiceScope.SendAsync(new UpdateUserRole.Command(createdUser.Id, "Admin"));

        // Assert
        userReturned.Permissions.ShouldNotContain("manage_tenant");
        var saved = await testingServiceScope.SendAsync(new GetUser.Query(createdUser.Id));
        saved.Permissions.ShouldNotContain("manage_tenant");
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_cannot_be_demoted()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new UpdateUserRole.Command(owner.Id, "User"));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
        var role = await scope.ExecuteDbContextAsync(db => db.Users
            .IgnoreQueryFilters([QueryFilterNames.Tenant]).Where(u => u.Id == owner.Id).Select(u => u.Role.Value).SingleAsync());
        role.ShouldBe("Admin");
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_can_stay_an_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var result = await scope.SendAsync(new UpdateUserRole.Command(owner.Id, "Admin"));

        // Assert
        result.Role.ShouldBe("Admin");
    }

    [Test]
    public async Task two_admins_who_demote_each_other_at_one_time_leave_one_admin()
    {
        // Arrange
        var pause = PauseAtQuery.After("users.last_admin.another");
        await using var services = pause.BuildServices();
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var first, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var second, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(first);
        await scope.AddSignInLinkAsync(second);
        var firstScope = new TestingServiceScope(services);
        var secondScope = new TestingServiceScope();
        firstScope.SetUserWithDefaults(identifier: second.Identifier);
        secondScope.SetUserWithDefaults(identifier: first.Identifier);

        // Act
        var demoteFirst = firstScope.SendAsync(new UpdateUserRole.Command(first.Id, "User"));
        await pause.ReachedAsync();
        var demoteSecond = secondScope.SendAsync(new UpdateUserRole.Command(second.Id, "User"));
        await pause.WaitUntilBlockedAsync(demoteSecond, () => RootLock.Tenant.HasWaiter(tenant.Id));
        pause.Release();

        // Assert
        await demoteFirst;
        await Should.ThrowAsync<ConflictException>(demoteSecond);
    }

    [Test]
    public async Task an_admin_who_cannot_manage_access_does_not_count_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var other, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddSignInLinkAsync(other);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        await scope.SendAsync(new RemoveUserPermission.Command(other.Id, Permission.ManageUserAccess().Value));

        // Act
        var act = () => scope.SendAsync(new UpdateUserRole.Command(owner.Id, "User"));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    public async Task the_only_admin_who_can_manage_access_cannot_give_it_up()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new RemoveUserPermission.Command(owner.Id, Permission.ManageUserAccess().Value));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
        (await scope.SendAsync(new GetUser.Query(owner.Id))).Permissions.ShouldContain(Permission.ManageUserAccess().Value);
    }

    [Test]
    [Arguments("read_users")]
    [Arguments("read_agents")]
    public async Task the_only_admin_cannot_give_up_a_permission_it_needs_to_make_another_admin(string permission)
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new RemoveUserPermission.Command(owner.Id, permission));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
        (await scope.SendAsync(new GetUser.Query(owner.Id))).Permissions.ShouldContain(permission);
    }

    [Test]
    public async Task an_admin_with_a_permission_missing_does_not_count_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var other, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddSignInLinkAsync(other);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        await scope.SendAsync(new RemoveUserPermission.Command(other.Id, Permission.ReadUsers().Value));

        // Act
        var act = () => scope.SendAsync(new UpdateUserRole.Command(owner.Id, "User"));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    public async Task a_permission_of_a_member_can_go_with_no_regard_for_the_admins()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var member, u => u.Role = "User")
            .BuildAsync();
        await scope.AddSignInLinkAsync(member);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        await scope.SendAsync(new AddUserPermission.Command(member.Id, Permission.ReadUsers().Value));

        // Act
        var result = await scope.SendAsync(new RemoveUserPermission.Command(member.Id, Permission.ReadUsers().Value));

        // Assert
        result.Permissions.ShouldNotContain(Permission.ReadUsers().Value);
    }

    [Test]
    public async Task two_admins_who_remove_a_permission_of_each_other_at_one_time_leave_one_complete_admin()
    {
        // Arrange
        var pause = PauseAtQuery.After("users.last_admin.another");
        await using var services = pause.BuildServices();
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var first, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var second, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(first);
        await scope.AddSignInLinkAsync(second);
        var firstScope = new TestingServiceScope(services);
        var secondScope = new TestingServiceScope();
        firstScope.SetUserWithDefaults(identifier: second.Identifier);
        secondScope.SetUserWithDefaults(identifier: first.Identifier);

        // Act
        var removeFromFirst = firstScope.SendAsync(new RemoveUserPermission.Command(first.Id, Permission.ReadUsers().Value));
        await pause.ReachedAsync();
        var removeFromSecond = secondScope.SendAsync(new RemoveUserPermission.Command(second.Id, Permission.ReadUsers().Value));
        await pause.WaitUntilBlockedAsync(removeFromSecond, () => RootLock.Tenant.HasWaiter(tenant.Id));
        pause.Release();

        // Assert
        await removeFromFirst;
        await Should.ThrowAsync<ConflictException>(removeFromSecond);
    }
}
