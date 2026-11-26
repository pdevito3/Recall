namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.DomainEvents;
using Recall.SharedTestHelpers.Fakes.User;
using Shouldly;

public class UserRoleChangeTests
{
    [Test]
    public void the_permissions_of_two_roles_add_up_and_a_shared_one_counts_once()
    {
        // Arrange
        var user = UserWith(UserRole.UserManager(), UserRole.Auditor());

        // Act
        var permissions = user.Permissions();

        // Assert
        permissions.Select(p => p.Value).ShouldBe(
            ["manage_users", "manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents"],
            ignoreOrder: true);
    }

    [Test]
    public void a_role_removal_keeps_a_permission_that_another_role_gives()
    {
        // Arrange
        var user = UserWith(UserRole.UserManager(), UserRole.Auditor());

        // Act
        var change = user.RemoveRole(UserRole.Auditor());

        // Assert
        change.Removed!.Role.ShouldBe(UserRole.Auditor());
        user.Roles.Select(r => r.Role).ShouldBe([UserRole.UserManager()]);
        user.HasPermission(Permission.ReadUsers()).ShouldBeTrue();
        user.HasPermission(Permission.ReadAgents()).ShouldBeFalse();
        user.HasPermission(Permission.ReadAuditLog()).ShouldBeFalse();
    }

    [Test]
    public void a_role_removal_ends_a_deny_that_no_role_left_backs_and_keeps_one_that_another_role_backs()
    {
        // Arrange: UserManager and Auditor both give read_users, and only Auditor gives read_agents
        var user = UserWith(UserRole.UserManager(), UserRole.Auditor());
        user.RemovePermission(Permission.ReadUsers());
        user.RemovePermission(Permission.ReadAgents());

        // Act
        var change = user.RemoveRole(UserRole.Auditor());

        // Assert
        change.ClearedOverrides.Select(o => o.Permission).ShouldBe([Permission.ReadAgents()]);
        user.UserPermissions.Select(o => (o.Permission, o.Effect)).ShouldBe([(Permission.ReadUsers(), PermissionEffect.Deny)]);
        user.Permissions().ShouldBe([Permission.ManageUsers(), Permission.ManageUserAccess()], ignoreOrder: true);
    }

    [Test]
    public void a_role_removal_keeps_an_added_permission()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());
        user.AddPermission(Permission.ManageAgents());

        // Act
        user.RemoveRole(UserRole.Auditor());

        // Assert
        user.Roles.ShouldBeEmpty();
        user.Permissions().ShouldBe([Permission.ManageAgents()]);
    }

    [Test]
    public void a_role_add_ends_each_override_of_a_permission_of_the_role_and_keeps_the_others()
    {
        // Arrange
        var user = UserWith(UserRole.UserManager());
        user.RemovePermission(Permission.ManageUsers());
        user.RemovePermission(Permission.ReadUsers());
        user.AddPermission(Permission.ReadAuditLog());
        user.AddPermission(Permission.ManageAgents());

        // Act
        var change = user.AddRole(UserRole.Auditor());

        // Assert
        change.Added!.Role.ShouldBe(UserRole.Auditor());
        change.ClearedOverrides.Select(p => (p.Permission.Value, p.Effect)).ShouldBe(
            [("read_users", PermissionEffect.Deny), ("read_audit_log", PermissionEffect.Grant)], ignoreOrder: true);
        user.UserPermissions.Select(p => (p.Permission.Value, p.Effect)).ShouldBe(
            [("manage_users", PermissionEffect.Deny), ("manage_agents", PermissionEffect.Grant)], ignoreOrder: true);
        user.Permissions().Select(p => p.Value).ShouldBe(
            ["manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents", "manage_agents"],
            ignoreOrder: true);
    }

    [Test]
    public void a_second_add_of_a_held_role_gives_back_its_removed_permissions()
    {
        // Arrange
        var user = UserWith(UserRole.Admin());
        user.RemovePermission(Permission.ManageTenant());

        // Act
        var change = user.AddRole(UserRole.Admin());

        // Assert
        change.Added.ShouldBeNull();
        change.Changed.ShouldBeTrue();
        user.Roles.Count.ShouldBe(1);
        user.UserPermissions.ShouldBeEmpty();
        user.Permissions().ShouldBe(Permission.GetAll(), ignoreOrder: true);
    }

    [Test]
    public void a_role_add_queues_user_updated()
    {
        // Arrange
        var user = UserWith();

        // Act
        user.AddRole(UserRole.Admin());

        // Assert
        user.HasRole(UserRole.Admin()).ShouldBeTrue();
        user.Permissions().ShouldBe(Permission.GetAll(), ignoreOrder: true);
        user.DomainEvents.ShouldBe([new UserCreated(user.Id), new UserUpdated(user.Id)]);
    }

    [Test]
    public void an_add_of_a_held_role_with_no_override_changes_nothing_and_queues_no_event()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());
        user.AddPermission(Permission.ManageAgents());
        var overrides = user.UserPermissions.ToList();

        // Act
        var change = user.AddRole(UserRole.Auditor());

        // Assert
        change.Changed.ShouldBeFalse();
        user.UserPermissions.ShouldBe(overrides);
        user.DomainEvents.ShouldBe([new UserCreated(user.Id)]);
    }

    [Test]
    public void a_removal_of_a_role_that_the_user_lacks_changes_nothing_and_queues_no_event()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());

        // Act
        var change = user.RemoveRole(UserRole.Admin());

        // Assert
        change.Changed.ShouldBeFalse();
        user.Roles.Select(r => r.Role).ShouldBe([UserRole.Auditor()]);
        user.DomainEvents.ShouldBe([new UserCreated(user.Id)]);
    }

    [Test]
    public void an_added_permission_that_a_role_gives_stores_no_override()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());

        // Act
        var change = user.AddPermission(Permission.ReadUsers());

        // Assert
        change.Changed.ShouldBeFalse();
        user.UserPermissions.ShouldBeEmpty();
        user.HasPermission(Permission.ReadUsers()).ShouldBeTrue();
    }

    [Test]
    public void an_added_permission_that_no_role_gives_stores_a_grant()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());

        // Act
        user.AddPermission(Permission.ManageAgents());

        // Assert
        user.UserPermissions.Select(p => (p.Permission, p.Effect)).ShouldBe([(Permission.ManageAgents(), PermissionEffect.Grant)]);
        user.HasPermission(Permission.ManageAgents()).ShouldBeTrue();
    }

    [Test]
    public void a_removal_of_an_added_permission_ends_the_grant_and_stores_no_deny()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());
        user.AddPermission(Permission.ManageAgents());

        // Act
        var change = user.RemovePermission(Permission.ManageAgents());

        // Assert
        change.Removed!.Effect.ShouldBe(PermissionEffect.Grant);
        change.Added.ShouldBeNull();
        user.UserPermissions.ShouldBeEmpty();
        user.HasPermission(Permission.ManageAgents()).ShouldBeFalse();
    }

    [Test]
    public void a_removal_of_a_permission_of_a_role_stores_a_deny()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());

        // Act
        user.RemovePermission(Permission.ReadAgents());

        // Assert
        user.UserPermissions.Select(p => (p.Permission, p.Effect)).ShouldBe([(Permission.ReadAgents(), PermissionEffect.Deny)]);
        user.HasPermission(Permission.ReadAgents()).ShouldBeFalse();
        user.HasPermission(Permission.ReadUsers()).ShouldBeTrue();
    }

    [Test]
    public void a_removal_of_a_permission_that_the_user_lacks_stores_nothing()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());

        // Act
        var change = user.RemovePermission(Permission.ManageTenant());

        // Assert
        change.Changed.ShouldBeFalse();
        user.UserPermissions.ShouldBeEmpty();
    }

    [Test]
    public void an_add_of_a_removed_permission_ends_the_deny()
    {
        // Arrange
        var user = UserWith(UserRole.Auditor());
        user.RemovePermission(Permission.ReadAgents());

        // Act
        var change = user.AddPermission(Permission.ReadAgents());

        // Assert
        change.Removed!.Effect.ShouldBe(PermissionEffect.Deny);
        change.Added.ShouldBeNull();
        user.UserPermissions.ShouldBeEmpty();
        user.HasPermission(Permission.ReadAgents()).ShouldBeTrue();
    }

    private static User UserWith(params UserRole[] roles)
        => User.Create(new FakeUserForCreation().Generate() with { Roles = roles.Select(r => r.Value).ToList() });
}
