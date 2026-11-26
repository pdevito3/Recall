namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Domain;
using Recall.Server.Domain.EmailAddresses;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class AddUserCommandTests : TestBase
{
    [Test]
    public async Task can_add_new_user_to_db()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var fakeUserDto = new FakeUserForCreationDto().Generate();

        // Act
        var command = new AddUser.Command(fakeUserDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.FirstOrDefaultAsync(u => u.Id == userReturned.Id));

        userInDb.ShouldNotBeNull();
        userInDb.FirstName.ShouldBe(fakeUserDto.FirstName);
        userInDb.LastName.ShouldBe(fakeUserDto.LastName);
        userInDb.Email!.Value.ShouldBe(fakeUserDto.Email);
        userInDb.Username.ShouldBe(fakeUserDto.Username);
        userInDb.Identifier.ShouldBe(userInDb.Id);
        userInDb.TenantId.ShouldBe(TestFixture.DefaultTenant.Id);
    }

    [Test]
    public async Task can_add_a_user_whose_text_fills_each_column_with_characters_outside_the_basic_plane()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F600", count));
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.FirstName, _ => Emoji(User.FirstNameMaxLength))
            .RuleFor(x => x.LastName, _ => Emoji(User.LastNameMaxLength))
            .RuleFor(x => x.Username, _ => Emoji(User.UsernameMaxLength))
            .RuleFor(x => x.Email, _ => Emoji(EmailAddress.MaxLength - "@example.com".Length) + "@example.com")
            .Generate();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.FirstOrDefaultAsync(u => u.Id == userReturned.Id));

        userInDb.ShouldNotBeNull();
        userInDb.FirstName.ShouldBe(fakeUserDto.FirstName);
        userInDb.LastName.ShouldBe(fakeUserDto.LastName);
        userInDb.Username.ShouldBe(fakeUserDto.Username);
        userInDb.Email!.Value.ShouldBe(fakeUserDto.Email);
    }

    [Test]
    public async Task can_add_user_with_admin_role()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.Roles, _ => ["Admin"])
            .Generate();

        // Act
        var command = new AddUser.Command(fakeUserDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users
                .Include(u => u.Roles)
                .Include(u => u.UserPermissions)
                .FirstOrDefaultAsync(u => u.Id == userReturned.Id));

        userInDb.ShouldNotBeNull();
        userInDb.Roles.Select(r => r.Role.Value).ShouldBe(["Admin"]);
        userInDb.UserPermissions.ShouldBeEmpty();
        userInDb.HasPermission(Permission.ManageTenant()).ShouldBeTrue();
        userReturned.Permissions.ShouldBe(Permission.ListNames(), ignoreOrder: true);
    }

    [Test]
    public async Task can_add_user_with_two_roles()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.Roles, _ => ["UserManager", "Auditor"])
            .Generate();

        // Act
        var userReturned = await testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        userReturned.Roles.ShouldBe(["UserManager", "Auditor"], ignoreOrder: true);
        userReturned.Permissions.ShouldBe(
            ["manage_users", "manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents"],
            ignoreOrder: true);
        userReturned.AddedPermissions.ShouldBeEmpty();
        userReturned.RemovedPermissions.ShouldBeEmpty();
    }

    [Test]
    public async Task can_add_a_member_with_no_role()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.Roles, _ => [])
            .Generate();

        // Act
        var command = new AddUser.Command(fakeUserDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users
                .Include(u => u.Roles)
                .Include(u => u.UserPermissions)
                .FirstOrDefaultAsync(u => u.Id == userReturned.Id));

        userInDb.ShouldNotBeNull();
        userInDb.Roles.ShouldBeEmpty();
        userInDb.UserPermissions.ShouldBeEmpty();
        userReturned.Permissions.ShouldBeEmpty();
    }

    [Test]
    public async Task cannot_add_user_to_another_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddTenant(out var otherTenant)
            .BuildAsync();
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.TenantId, _ => otherTenant.Id)
            .Generate();

        // Act
        var act = () => testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task user_with_no_user_record_cannot_add_user()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        testingServiceScope.SetUserWithDefaults(identifier: $"unknown-{Guid.NewGuid()}");
        var fakeUserDto = new FakeUserForCreationDto().Generate();

        // Act
        var act = () => testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task cannot_add_user_to_an_unknown_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        testingServiceScope.SetMachineClient(TestFixture.TrustedMachineClientId);
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.TenantId, _ => EntityId.New(Tenant.IdPrefix))
            .Generate();

        // Act
        var act = () => testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        var exception = await act.ShouldThrowAsync<ValidationException>();
        exception.Message.ShouldContain("The tenant does not exist.");
        await ShouldHaveNoUserAsync(testingServiceScope, fakeUserDto.TenantId!);
    }

    [Test]
    public async Task cannot_add_user_to_a_soft_deleted_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        testingServiceScope.SetMachineClient(TestFixture.TrustedMachineClientId);
        await testingServiceScope.StartGraph(out _)
            .AddTenant(out var tenant)
            .BuildAsync();
        await testingServiceScope.SendAsync(new DeleteTenant.Command(tenant.Id));
        var fakeUserDto = new FakeUserForCreationDto()
            .RuleFor(x => x.TenantId, _ => tenant.Id)
            .Generate();

        // Act
        var act = () => testingServiceScope.SendAsync(new AddUser.Command(fakeUserDto));

        // Assert
        var exception = await act.ShouldThrowAsync<ValidationException>();
        exception.Message.ShouldContain("The tenant does not exist.");
        await ShouldHaveNoUserAsync(testingServiceScope, fakeUserDto.TenantId!);
    }

    [Test]
    public async Task queues_domain_event_on_create()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var fakeUserDto = new FakeUserForCreationDto().Generate();

        // Act
        var command = new AddUser.Command(fakeUserDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        userReturned.ShouldNotBeNull();
        userReturned.Id.ShouldStartWith($"{User.IdPrefix}_");
    }

    private static async Task ShouldHaveNoUserAsync(TestingServiceScope testingServiceScope, string tenantId)
    {
        var userExists = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenantId));
        userExists.ShouldBeFalse();
    }
}
