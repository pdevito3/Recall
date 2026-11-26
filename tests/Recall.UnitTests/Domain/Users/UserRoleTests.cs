namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Shouldly;

public class UserRoleTests
{
    [Test]
    public void can_create_each_role()
    {
        // Arrange & Act
        var admin = UserRole.Admin();
        var userManager = UserRole.UserManager();
        var auditor = UserRole.Auditor();

        // Assert
        admin.Value.ShouldBe("Admin");
        userManager.Value.ShouldBe("UserManager");
        auditor.Value.ShouldBe("Auditor");
    }

    [Test]
    public void can_create_role_from_string()
    {
        // Arrange & Act
        var adminRole = UserRole.Of("Admin");
        var auditorRole = UserRole.Of("Auditor");

        // Assert
        adminRole.Value.ShouldBe("Admin");
        auditorRole.Value.ShouldBe("Auditor");
    }

    [Test]
    public void role_is_case_insensitive()
    {
        // Arrange & Act
        var adminLower = UserRole.Of("admin");
        var adminUpper = UserRole.Of("ADMIN");
        var adminMixed = UserRole.Of("AdMiN");

        // Assert
        adminLower.Value.ShouldBe("Admin");
        adminUpper.Value.ShouldBe("Admin");
        adminMixed.Value.ShouldBe("Admin");
    }

    [Test]
    public void invalid_role_throws_validation_exception()
    {
        // Arrange & Act & Assert
        Should.Throw<ValidationException>(() => UserRole.Of("InvalidRole"));
        Should.Throw<ValidationException>(() => UserRole.Of(""));
        Should.Throw<ValidationException>(() => UserRole.Of(null!));
    }

    [Test]
    public void the_old_role_user_is_no_role()
    {
        // Act & Assert
        Should.Throw<ValidationException>(() => UserRole.Of("User"));
    }

    [Test]
    public void an_invalid_item_of_a_list_names_its_request_field()
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => UserRole.Of("Owner", "roles[1]"));

        // Assert
        exception.Property.ShouldBe("roles[1]");
    }

    [Test]
    public void admin_role_has_every_permission()
    {
        // Act
        var permissions = UserRole.Admin().Permissions();

        // Assert
        permissions.ShouldBe(Permission.GetAll(), ignoreOrder: true);
    }

    [Test]
    public void user_manager_role_manages_and_reads_users()
    {
        // Act
        var permissions = UserRole.UserManager().Permissions();

        // Assert
        permissions.Select(p => p.Value).ShouldBe(
            ["manage_users", "manage_user_access", "read_users"], ignoreOrder: true);
    }

    [Test]
    public void auditor_role_reads_the_audit_log_the_users_and_the_agents()
    {
        // Act
        var permissions = UserRole.Auditor().Permissions();

        // Assert
        permissions.Select(p => p.Value).ShouldBe(
            ["read_audit_log", "read_audit_log_sensitive", "read_users", "read_agents"], ignoreOrder: true);
    }

    [Test]
    public void list_names_returns_all_role_names()
    {
        // Arrange & Act
        var names = UserRole.ListNames();

        // Assert
        names.ShouldBe(["Admin", "UserManager", "Auditor"], ignoreOrder: true);
    }
}
