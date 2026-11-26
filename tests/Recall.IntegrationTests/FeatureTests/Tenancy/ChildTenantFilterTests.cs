namespace Recall.IntegrationTests.FeatureTests.Tenancy;

using Recall.Server.Domain.Users;
using Recall.Server.Resources;
using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class ChildTenantFilterTests : TestBase
{
    [Test]
    public async Task caller_of_one_tenant_sees_no_role_or_permission_row_of_another_tenant()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        var graph = testingServiceScope.StartGraph(out _)
            .AddTenant(out var otherTenant)
            .AddUser(out var otherUser, u => u.Roles = ["Admin"]);
        otherUser.RemovePermission(Permission.ManageTenant());
        await graph
            .Under(otherTenant)
            .AddAgent(out var otherAgent, a => a.Permissions = [Permission.Names.ManageTenant])
            .BuildAsync();
        await testingServiceScope.SetupTenantAsync();

        // Act
        var userPermissionsOfAllTenants = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.UserPermissions.IgnoreQueryFilters([QueryFilterNames.Tenant]).CountAsync(p => p.UserId == otherUser.Id));
        var agentPermissionsOfAllTenants = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.AgentPermissions.IgnoreQueryFilters([QueryFilterNames.Tenant]).CountAsync(p => p.AgentId == otherAgent.Id));
        var userPermissions = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.UserPermissions.Where(p => p.UserId == otherUser.Id).ToListAsync());
        var agentPermissions = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.AgentPermissions.Where(p => p.AgentId == otherAgent.Id).ToListAsync());
        var userRolesOfAllTenants = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.UserRoles.IgnoreQueryFilters([QueryFilterNames.Tenant]).CountAsync(r => r.UserId == otherUser.Id));
        var userRoles = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.UserRoles.Where(r => r.UserId == otherUser.Id).ToListAsync());

        // Assert
        userPermissionsOfAllTenants.ShouldBe(1);
        userRolesOfAllTenants.ShouldBe(1);
        userRoles.ShouldBeEmpty();
        agentPermissionsOfAllTenants.ShouldBe(1);
        userPermissions.ShouldBeEmpty();
        agentPermissions.ShouldBeEmpty();
    }
}
