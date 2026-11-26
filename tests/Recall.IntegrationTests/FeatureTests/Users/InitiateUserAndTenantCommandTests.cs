namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class InitiateUserAndTenantCommandTests : TestBase
{
    [Test]
    public async Task user_with_no_user_record_cannot_start_a_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var dto = new FakeInitiateUserAndTenantDto().Generate();
        testingServiceScope.SetUserWithDefaults(identifier: $"unknown-{Guid.NewGuid()}");

        // Act
        var act = () => testingServiceScope.SendAsync(new InitiateUserAndTenant.Command(dto));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task trusted_client_can_start_a_tenant_with_a_new_user()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var dto = new FakeInitiateUserAndTenantDto().Generate();
        testingServiceScope.SetMachineClient(TestFixture.TrustedMachineClientId);

        // Act
        var result = await testingServiceScope.SendAsync(new InitiateUserAndTenant.Command(dto));

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == result.User.Id));
        userInDb.ShouldNotBeNull();
        userInDb.Identifier.ShouldBe(userInDb.Id);
        userInDb.TenantId.ShouldBe(result.Tenant.Id);
    }

    [Test]
    public async Task new_admin_is_the_caller_of_a_session_whose_sub_is_their_id()
    {
        // Arrange
        var initiator = new TestingServiceScope();
        initiator.SetMachineClient(TestFixture.TrustedMachineClientId);
        var started = await initiator.SendAsync(new InitiateUserAndTenant.Command(
            new FakeInitiateUserAndTenantDto().Generate() with { Roles = ["Admin"] }));
        var session = new TestingServiceScope();
        session.SetUserWithDefaults(identifier: started.User.Id);

        // Act
        var caller = await session.GetService<IPermissionGuard>().GetCallerAsync(CancellationToken.None);

        // Assert
        caller.ShouldBeOfType<Caller.Member>().UserId.ShouldBe(started.User.Id);
        started.User.Identifier.ShouldBe(started.User.Id);
    }

    [Test]
    public async Task new_admin_gets_each_action_for_their_own_record()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var dto = new FakeInitiateUserAndTenantDto().Generate() with { Roles = ["Admin"] };
        testingServiceScope.SetMachineClient(TestFixture.TrustedMachineClientId);

        // Act
        var result = await testingServiceScope.SendAsync(new InitiateUserAndTenant.Command(dto));

        // Assert
        result.User.AllowedActions.ShouldBe(["Update", "AddRole", "RemoveRole", "AddPermission", "RemovePermission", "Delete", "IssueTemporaryPassword", "ManageExternalLogins"]);
    }

    [Test]
    public async Task user_of_a_deleted_tenant_can_start_a_new_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        testingServiceScope.SetMachineClient(TestFixture.TrustedMachineClientId);
        await testingServiceScope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var user)
            .BuildAsync();
        await testingServiceScope.SendAsync(new DeleteTenant.Command(tenant.Id));
        var dto = new FakeInitiateUserAndTenantDto().Generate();

        // Act
        var result = await testingServiceScope.SendAsync(new InitiateUserAndTenant.Command(dto));

        // Assert
        result.Tenant.Id.ShouldNotBe(tenant.Id);
        result.User.Id.ShouldNotBe(user.Id);
        result.User.TenantId.ShouldBe(result.Tenant.Id);
        result.User.Identifier.ShouldBe(result.User.Id);
        var oldUser = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id));
        oldUser.IsDeleted.ShouldBeTrue();
        oldUser.TenantId.ShouldBe(tenant.Id);
    }
}
