namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Users;
using Shouldly;

public class EffectivePermissionsTests
{
    [Test]
    public void held_permissions_are_the_roles_with_the_added_and_less_the_removed()
    {
        // Act
        var held = EffectivePermissions.Of(
            [UserRole.UserManager(), UserRole.Auditor()],
            added: [Permission.ManageAgents()],
            removed: [Permission.ReadUsers(), Permission.ReadAuditLogSensitive()]);

        // Assert
        held.Select(p => p.Value).ShouldBe(
            ["manage_users", "manage_user_access", "read_audit_log", "read_agents", "manage_agents"], ignoreOrder: true);
    }

    [Test]
    public void a_removal_wins_over_an_addition_of_the_same_permission()
    {
        // Act
        var held = EffectivePermissions.Of([], added: [Permission.ManageTenant()], removed: [Permission.ManageTenant()]);

        // Assert
        held.ShouldBeEmpty();
    }

    [Test]
    public void no_role_holds_no_permission()
    {
        // Act
        var held = EffectivePermissions.FromRoles([]);

        // Assert
        held.ShouldBeEmpty();
    }
}
