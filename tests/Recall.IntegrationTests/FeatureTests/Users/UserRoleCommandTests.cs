namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.Server.Resources;
using Recall.IntegrationTests.Databases;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

/// <summary>
/// AddUserRole and RemoveUserRole, and the rule that a tenant keeps an Admin who has no removed permission and can
/// sign in (<see cref="LastAdmin"/>).
/// </summary>
public class UserRoleCommandTests : TestBase
{
    private static readonly string[] UserManagerAndAuditor =
        ["manage_users", "manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents"];

    [Test]
    [Property("Rule", "UM-2")]
    public async Task member_cannot_add_a_role_to_themselves()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var member, u => u.Roles = [])
            .BuildAsync();
        testingServiceScope.SetUserWithDefaults(identifier: member.Identifier);

        // Act
        var act = () => testingServiceScope.SendAsync(new AddUserRole.Command(member.Id, "Admin"));

        // Assert
        (await act.ShouldThrowAsync<ForbiddenAccessException>()).Message
            .ShouldBe("You cannot manage the access of users. Ask an admin for access.");
        (await RolesOfAsync(member.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task can_add_the_role_admin_to_a_member()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = [])
            .BuildAsync();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, "Admin"));

        // Assert
        userReturned.Roles.ShouldBe(["Admin"]);
        userReturned.Permissions.ShouldBe(Permission.ListNames(), ignoreOrder: true);
        (await RolesOfAsync(createdUser.Id)).ShouldBe(["Admin"]);
        // The role gives the permissions, so the add stores no permission row.
        (await OverridesOfAsync(createdUser.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task the_permissions_of_two_roles_add_up_and_a_removal_keeps_the_ones_that_the_other_role_gives()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = [])
            .BuildAsync();
        await testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, "UserManager"));

        // Act
        var both = await testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, "Auditor"));
        var afterRemoval = await testingServiceScope.SendAsync(new RemoveUserRole.Command(createdUser.Id, "Auditor"));

        // Assert
        both.Roles.ShouldBe(["UserManager", "Auditor"], ignoreOrder: true);
        both.Permissions.ShouldBe(UserManagerAndAuditor, ignoreOrder: true);
        afterRemoval.Roles.ShouldBe(["UserManager"]);
        afterRemoval.Permissions.ShouldBe(["manage_users", "manage_user_access", "read_users"], ignoreOrder: true);
        (await RolesOfAsync(createdUser.Id)).ShouldBe(["UserManager"]);
    }

    [Test]
    public async Task a_role_add_ends_each_override_of_a_permission_of_the_role_and_keeps_the_others()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var graph = testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = ["UserManager"]);
        createdUser.RemovePermission(Permission.ManageUsers());
        createdUser.RemovePermission(Permission.ReadUsers());
        createdUser.AddPermissions([Permission.ReadAuditLog(), Permission.ManageAgents()]);
        await graph.BuildAsync();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, "Auditor"));

        // Assert
        userReturned.AddedPermissions.ShouldBe(["manage_agents"]);
        userReturned.RemovedPermissions.ShouldBe(["manage_users"]);
        userReturned.Permissions.ShouldBe(
            ["manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents", "manage_agents"],
            ignoreOrder: true);
        (await OverridesOfAsync(createdUser.Id)).ShouldBe(
            [("manage_users", PermissionEffect.Deny), ("manage_agents", PermissionEffect.Grant)], ignoreOrder: true);
    }

    [Test]
    public async Task a_second_add_of_a_held_role_gives_back_its_removed_permissions()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = ["Admin"])
            .BuildAsync();
        await testingServiceScope.SendAsync(new RemoveUserPermission.Command(createdUser.Id, "manage_tenant"));

        // Act
        var userReturned = await testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, "Admin"));

        // Assert
        userReturned.Roles.ShouldBe(["Admin"]);
        userReturned.Permissions.ShouldBe(Permission.ListNames(), ignoreOrder: true);
        userReturned.RemovedPermissions.ShouldBeEmpty();
        (await OverridesOfAsync(createdUser.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task can_remove_the_role_admin_from_an_admin()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = ["Admin"])
            .BuildAsync();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new RemoveUserRole.Command(createdUser.Id, "Admin"));

        // Assert
        userReturned.Roles.ShouldBeEmpty();
        userReturned.Permissions.ShouldBeEmpty();
        (await RolesOfAsync(createdUser.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task a_role_removal_keeps_an_added_permission()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var graph = testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = ["Auditor"]);
        createdUser.AddPermissions([Permission.ManageAgents()]);
        await graph.BuildAsync();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new RemoveUserRole.Command(createdUser.Id, "Auditor"));

        // Assert
        userReturned.Roles.ShouldBeEmpty();
        userReturned.Permissions.ShouldBe(["manage_agents"]);
    }

    [Test]
    public async Task a_removal_of_a_role_that_the_user_lacks_changes_nothing()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u => u.Roles = ["Auditor"])
            .BuildAsync();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new RemoveUserRole.Command(createdUser.Id, "Admin"));

        // Assert
        userReturned.Roles.ShouldBe(["Auditor"]);
        userReturned.Version.ShouldBe(createdUser.Version);
    }

    [Test]
    public async Task throws_when_user_not_found()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var nonExistentId = EntityId.New(User.IdPrefix);

        // Act
        var add = () => testingServiceScope.SendAsync(new AddUserRole.Command(nonExistentId, "Admin"));
        var remove = () => testingServiceScope.SendAsync(new RemoveUserRole.Command(nonExistentId, "Admin"));

        // Assert
        await add.ShouldThrowAsync<NotFoundException>();
        await remove.ShouldThrowAsync<NotFoundException>();
    }

    [Test]
    [Arguments("InvalidRole")]
    [Arguments("User")]
    public async Task throws_when_invalid_role(string role)
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser)
            .BuildAsync();

        // Act
        var add = () => testingServiceScope.SendAsync(new AddUserRole.Command(createdUser.Id, role));
        var remove = () => testingServiceScope.SendAsync(new RemoveUserRole.Command(createdUser.Id, role));

        // Assert
        await add.ShouldThrowAsync<ValidationException>();
        await remove.ShouldThrowAsync<ValidationException>();
    }

    [Test]
    [Property("Rule", "UM-2")]
    public async Task a_user_manager_cannot_add_the_role_admin()
    {
        // Arrange
        var (scope, _, target) = await ArrangeUserManagerAndTargetAsync([]);

        // Act
        var act = () => scope.SendAsync(new AddUserRole.Command(target.Id, "Admin"));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
        (await RolesOfAsync(target.Id)).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "UM-2")]
    public async Task a_user_manager_can_add_the_role_user_manager_and_not_the_role_auditor()
    {
        // Arrange
        var (scope, _, target) = await ArrangeUserManagerAndTargetAsync([]);

        // Act
        var added = await scope.SendAsync(new AddUserRole.Command(target.Id, "UserManager"));
        var act = () => scope.SendAsync(new AddUserRole.Command(target.Id, "Auditor"));

        // Assert
        added.Roles.ShouldBe(["UserManager"]);
        (await act.ShouldThrowAsync<ForbiddenAccessException>()).Message
            .ShouldBe("You cannot read the audit log. Ask an admin for access.");
        (await RolesOfAsync(target.Id)).ShouldBe(["UserManager"]);
    }

    [Test]
    [Property("Rule", "UM-2")]
    public async Task a_user_manager_cannot_remove_a_role_from_an_admin()
    {
        // Arrange
        var (scope, _, target) = await ArrangeUserManagerAndTargetAsync(["Admin", "UserManager"]);

        // Act
        var act = () => scope.SendAsync(new RemoveUserRole.Command(target.Id, "UserManager"));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
        (await RolesOfAsync(target.Id)).ShouldBe(["Admin", "UserManager"], ignoreOrder: true);
    }

    [Test]
    [Property("Rule", "UM-2")]
    public async Task a_user_manager_can_remove_a_role_from_a_peer_whose_permissions_it_holds()
    {
        // Arrange
        var (scope, _, target) = await ArrangeUserManagerAndTargetAsync(["UserManager"]);

        // Act
        var userReturned = await scope.SendAsync(new RemoveUserRole.Command(target.Id, "UserManager"));

        // Assert
        userReturned.Roles.ShouldBeEmpty();
        userReturned.Permissions.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "UM-2")]
    public async Task a_caller_with_manage_user_access_and_no_read_users_cannot_change_a_role_of_another_user()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var graph = testingServiceScope.StartGraph(out _)
            .AddUsers(out var users, u => u.Roles = [], u => u.Roles = []);
        users[0].AddPermissions([Permission.ManageUserAccess()]);
        await graph.BuildAsync();
        testingServiceScope.SetUserWithDefaults(identifier: users[0].Identifier);

        // Act
        var add = () => testingServiceScope.SendAsync(new AddUserRole.Command(users[1].Id, "UserManager"));
        var remove = () => testingServiceScope.SendAsync(new RemoveUserRole.Command(users[1].Id, "UserManager"));

        // Assert
        (await add.ShouldThrowAsync<ForbiddenAccessException>()).Message
            .ShouldBe("You cannot see the users. Ask an admin for access.");
        (await remove.ShouldThrowAsync<ForbiddenAccessException>()).Message
            .ShouldBe("You cannot see the users. Ask an admin for access.");
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_cannot_lose_the_role_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new RemoveUserRole.Command(owner.Id, "Admin"));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
        (await RolesOfAsync(owner.Id)).ShouldBe(["Admin"]);
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_can_lose_another_role()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Roles = ["Admin", "Auditor"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var result = await scope.SendAsync(new RemoveUserRole.Command(owner.Id, "Auditor"));

        // Assert
        result.Roles.ShouldBe(["Admin"]);
        result.Permissions.ShouldBe(Permission.ListNames(), ignoreOrder: true);
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_can_add_the_role_admin_again()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var result = await scope.SendAsync(new AddUserRole.Command(owner.Id, "Admin"));

        // Assert
        result.Roles.ShouldBe(["Admin"]);
    }

    [Test]
    public async Task two_admins_who_remove_the_role_admin_from_each_other_at_one_time_leave_one_admin()
    {
        // Arrange
        var pause = PauseAtQuery.After("users.last_admin.another");
        await using var services = pause.BuildServices();
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var first, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var second, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(first);
        await scope.AddSignInLinkAsync(second);
        var firstScope = new TestingServiceScope(services);
        var secondScope = new TestingServiceScope();
        firstScope.SetUserWithDefaults(identifier: second.Identifier);
        secondScope.SetUserWithDefaults(identifier: first.Identifier);

        // Act
        var demoteFirst = firstScope.SendAsync(new RemoveUserRole.Command(first.Id, "Admin"));
        await pause.ReachedAsync();
        var demoteSecond = secondScope.SendAsync(new RemoveUserRole.Command(second.Id, "Admin"));
        await pause.WaitUntilBlockedAsync(demoteSecond, () => RootLock.Tenant.HasWaiter(tenant.Id));
        pause.Release();

        // Assert
        await demoteFirst;
        await Should.ThrowAsync<ConflictException>(demoteSecond);
    }

    [Test]
    [Arguments("manage_user_access")]
    [Arguments("read_users")]
    public async Task an_admin_with_a_removed_permission_does_not_count_as_another_admin(string removed)
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var other, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddSignInLinkAsync(other);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        await scope.SendAsync(new RemoveUserPermission.Command(other.Id, removed));

        // Act
        var act = () => scope.SendAsync(new RemoveUserRole.Command(owner.Id, "Admin"));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    public async Task an_admin_whose_removed_permission_came_back_counts_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var other, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddSignInLinkAsync(other);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        await scope.SendAsync(new RemoveUserPermission.Command(other.Id, "read_users"));
        await scope.SendAsync(new AddUserPermission.Command(other.Id, "read_users"));

        // Act
        var result = await scope.SendAsync(new RemoveUserRole.Command(owner.Id, "Admin"));

        // Assert
        result.Roles.ShouldBeEmpty();
    }

    [Test]
    public async Task the_only_admin_who_can_manage_access_cannot_give_it_up()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
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
            .AddUser(out var owner, u => u.Roles = ["Admin"])
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
    public async Task a_permission_of_a_member_can_go_with_no_regard_for_the_admins()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var member, u => u.Roles = [])
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
            .AddUser(out var first, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var second, u => u.Roles = ["Admin"])
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
    // A caller that holds the permissions of the role UserManager, and a user of its tenant with the roles.
    private static async Task<(TestingServiceScope Scope, User Caller, User Target)> ArrangeUserManagerAndTargetAsync(
        IReadOnlyList<string> targetRoles)
    {
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Roles = ["UserManager"], u => u.Roles = targetRoles)
            .BuildAsync();
        scope.SetUserWithDefaults(identifier: users[0].Identifier);
        return (scope, users[0], users[1]);
    }

    private static Task<List<string>> RolesOfAsync(string userId)
        => new TestingServiceScope().ExecuteDbContextAsync(db => db.UserRoles
            .IgnoreQueryFilters([QueryFilterNames.Tenant])
            .Where(r => r.UserId == userId).Select(r => r.Role.Value).ToListAsync());

    private static async Task<List<(string Permission, PermissionEffect Effect)>> OverridesOfAsync(string userId)
        => (await new TestingServiceScope().ExecuteDbContextAsync(db => db.UserPermissions
                .IgnoreQueryFilters([QueryFilterNames.Tenant])
                .Where(p => p.UserId == userId).Select(p => new { p.Permission, p.Effect }).ToListAsync()))
            .Select(p => (p.Permission.Value, p.Effect)).ToList();
}
