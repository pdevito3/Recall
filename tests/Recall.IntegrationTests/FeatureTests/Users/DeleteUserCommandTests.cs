namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.IntegrationTests.Databases;
using Recall.Server.Databases;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.Server.Resources;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class DeleteUserCommandTests : TestBase
{
    [Test]
    [Property("Rule", "SES-49")]
    public async Task can_delete_existing_user_and_the_row_keeps_no_name_email_or_username()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser, u =>
            {
                u.FirstName = "Ada";
                u.LastName = "Lovelace";
                u.Email = "ada@example.com";
                u.Username = "ada";
            })
            .BuildAsync();

        // Act
        var command = new DeleteUser.Command(createdUser.Id);
        await testingServiceScope.SendAsync(command);

        // Assert - User should be soft deleted
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == createdUser.Id));

        userInDb.ShouldNotBeNull();
        userInDb.IsDeleted.ShouldBeTrue();
        userInDb.FirstName.ShouldBeEmpty();
        userInDb.LastName.ShouldBeNull();
        userInDb.Email.ShouldBeNull();
        userInDb.Username.ShouldBeEmpty();
        userInDb.Identifier.ShouldBe(createdUser.Identifier);
    }

    [Test]
    public async Task deleted_user_not_returned_in_queries()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var createdUser)
            .BuildAsync();

        // Act
        var deleteCommand = new DeleteUser.Command(createdUser.Id);
        await testingServiceScope.SendAsync(deleteCommand);

        // Assert - User should not be found without IgnoreQueryFilters
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.FirstOrDefaultAsync(u => u.Id == createdUser.Id));

        userInDb.ShouldBeNull();
    }

    [Test]
    public async Task the_only_admin_who_can_sign_in_cannot_be_deleted()
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
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LastAdmin.RefusedMessage);
        (await LiveAsync(scope, owner.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task an_admin_can_be_deleted_when_another_admin_can_sign_in()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var other, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(other);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        await scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        (await LiveAsync(scope, owner.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task the_only_admin_of_a_tenant_cannot_be_deleted_also_with_no_sign_in_link()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task an_admin_with_no_sign_in_link_can_be_deleted_when_another_admin_stays()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out var other, u => u.Role = "Admin")
            .BuildAsync();
        scope.SetUserWithDefaults(identifier: other.Identifier);

        // Act
        await scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        (await LiveAsync(scope, owner.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task an_admin_with_no_sign_in_link_does_not_count_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Role = "Admin")
            .Under(tenant)
            .AddUser(out _, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task a_person_with_a_sign_in_link_and_the_role_user_does_not_count_as_another_admin()
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
        foreach (var permission in Permission.GetAll())
            await scope.SendAsync(new AddUserPermission.Command(member.Id, permission.Value));

        // Act
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task an_admin_of_another_tenant_does_not_count_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var elsewhere, u => u.Role = "Admin")
            .BuildAsync();
        await scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUser(out var owner, u => u.Role = "Admin")
            .BuildAsync();
        await scope.AddSignInLinkAsync(elsewhere);
        scope.SetMachineClient(TestFixture.TrustedMachineClientId);

        // Act
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task two_admins_who_delete_each_other_at_one_time_leave_one_admin()
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

        // Act
        var firstScope = new TestingServiceScope(services);
        var secondScope = new TestingServiceScope();
        firstScope.SetUserWithDefaults(identifier: second.Identifier);
        secondScope.SetUserWithDefaults(identifier: first.Identifier);
        var deleteFirst = firstScope.SendAsync(new DeleteUser.Command(first.Id));
        await pause.ReachedAsync();
        var deleteSecond = secondScope.SendAsync(new DeleteUser.Command(second.Id));
        await pause.WaitUntilBlockedAsync(deleteSecond, () => RootLock.Tenant.HasWaiter(tenant.Id));
        pause.Release();

        // Assert
        await deleteFirst;
        await Should.ThrowAsync<ConflictException>(deleteSecond);
        (await LiveAsync(scope, first.Id)).ShouldBeFalse();
        (await LiveAsync(scope, second.Id)).ShouldBeTrue();
    }

    private static Task<bool> LiveAsync(TestingServiceScope scope, string userId)
        => scope.ExecuteDbContextAsync(db => db.Users.IgnoreQueryFilters([QueryFilterNames.Tenant]).AnyAsync(u => u.Id == userId));
}
