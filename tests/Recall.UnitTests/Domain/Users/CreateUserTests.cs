namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.DomainEvents;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.Fakes.User;
using Shouldly;

public class CreateUserTests
{
    [Test]
    public void can_create_valid_user()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation().Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.FirstName.ShouldBe(userForCreation.FirstName);
        user.LastName.ShouldBe(userForCreation.LastName);
        user.Identifier.ShouldBe(userForCreation.Identifier);
        user.Email!.Value.ShouldBe(userForCreation.Email);
        user.Username.ShouldBe(userForCreation.Username);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void cannot_create_user_with_blank_email(string email)
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Email, _ => email)
            .Generate();

        // Act
        var act = () => User.Create(userForCreation);

        // Assert
        act.ShouldThrow<ValidationException>().Message.ShouldBe("Please provide a valid email.");
    }

    [Test]
    public void can_create_user_with_no_email_and_no_last_name()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation().Generate() with { Email = null, LastName = null, FirstName = "Mina" };

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.Email.ShouldBeNull();
        user.LastName.ShouldBeNull();
        user.FullName.ShouldBe("Mina");
    }

    public static IEnumerable<(string?, string)> InvalidTenantIds =>
    [
        (null, "Please provide a tenant."),
        ($"{User.IdPrefix}_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f", "Please provide a valid tenant ID."),
        ($"{Tenant.IdPrefix}_{new string('a', 47)}", "Please provide a valid tenant ID."),
        ($"{Tenant.IdPrefix}_0199F0C1A2B37C4D8E9F0A1B2C3D4E5F", "Please provide a valid tenant ID."),
    ];

    [Test]
    [MethodDataSource(nameof(InvalidTenantIds))]
    public void cannot_create_user_with_invalid_tenant_id(string? tenantId, string message)
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.TenantId, _ => tenantId!)
            .Generate();

        // Act
        var act = () => User.Create(userForCreation);

        // Assert
        act.ShouldThrow<ValidationException>().Message.ShouldBe(message);
    }

    [Test]
    public void user_creation_queues_domain_event()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation().Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.DomainEvents.ShouldBe([new UserCreated(user.Id)]);
    }

    [Test]
    public void an_admin_holds_each_permission_through_the_role_with_no_override()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Roles, _ => ["Admin"])
            .Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.Roles.Select(r => r.Role.Value).ShouldBe(["Admin"]);
        user.UserPermissions.ShouldBeEmpty();
        user.Permissions().ShouldBe(Permission.GetAll(), ignoreOrder: true);
    }

    [Test]
    public void a_member_has_no_role_and_no_permission()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Roles, _ => [])
            .Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.Roles.ShouldBeEmpty();
        user.UserPermissions.ShouldBeEmpty();
        user.Permissions().ShouldBeEmpty();
    }

    [Test]
    public void a_role_named_twice_is_one_role()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Roles, _ => ["Auditor", "auditor", "UserManager"])
            .Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.Roles.Select(r => r.Role.Value).ShouldBe(["Auditor", "UserManager"], ignoreOrder: true);
    }

    [Test]
    public void an_invalid_role_names_its_item_of_the_list()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Roles, _ => ["Auditor", "User"])
            .Generate();

        // Act
        var exception = Should.Throw<ValidationException>(() => User.Create(userForCreation));

        // Assert
        exception.Property.ShouldBe("roles[1]");
    }

    [Test]
    public void user_full_name_returns_concatenated_name()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.FirstName, _ => "John")
            .RuleFor(x => x.LastName, _ => "Doe")
            .Generate();

        // Act
        var user = User.Create(userForCreation);

        // Assert
        user.FullName.ShouldBe("John Doe");
    }

    [Test]
    public void missing_first_name_throws_validation_exception()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.FirstName, _ => "")
            .Generate();

        // Act & Assert
        Should.Throw<ValidationException>(() => User.Create(userForCreation));
    }

    [Test]
    public void missing_last_name_throws_validation_exception()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.LastName, _ => "")
            .Generate();

        // Act & Assert
        Should.Throw<ValidationException>(() => User.Create(userForCreation));
    }

    [Test]
    public void missing_identifier_throws_validation_exception()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Identifier, _ => "")
            .Generate();

        // Act & Assert
        Should.Throw<ValidationException>(() => User.Create(userForCreation));
    }

    [Test]
    public void missing_username_throws_validation_exception()
    {
        // Arrange
        var userForCreation = new FakeUserForCreation()
            .RuleFor(x => x.Username, _ => "")
            .Generate();

        // Act & Assert
        Should.Throw<ValidationException>(() => User.Create(userForCreation));
    }
}
