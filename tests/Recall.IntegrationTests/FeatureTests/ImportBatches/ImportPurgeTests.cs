namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using static ImportScenario;

public class ImportPurgeTests : TestBase
{
    [Test]
    [Property("Rule", "IMP-24")]
    public async Task a_delete_of_a_user_removes_each_batch_of_the_user_and_its_rows()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = UserRole.Admin().Value)
            .Under(tenant)
            .AddUser(out var owner)
            .Under(tenant)
            .BuildAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 0, date: Day.AddDays(1)));
        await RejectDayAsync(owner, batch.Id, Day.AddDays(1));
        scope.SetUserWithDefaults(identifier: admin.Identifier);

        // Act
        await scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        var rows = await new TestingServiceScope().ExecuteDbContextAsync(async db => (
            await db.ImportBatches.IgnoreQueryFilters().CountAsync(b => b.UserId == owner.Id),
            await db.ImportEntries.IgnoreQueryFilters().CountAsync(e => e.UserId == owner.Id),
            await db.ImportDays.IgnoreQueryFilters().CountAsync(d => d.UserId == owner.Id)));
        rows.ShouldBe((0, 0, 0));
    }

    [Test]
    [Property("Rule", "IMP-24")]
    public async Task a_delete_of_a_tenant_removes_each_open_and_accepted_batch_of_its_users()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = UserRole.Admin().Value)
            .Under(tenant)
            .BuildAsync();
        var open = await OpenAsync(admin);
        await PutAsync(admin, open.Id, Entry("open", 0), Entry("held", 0, date: Day.AddDays(1)));
        await RejectDayAsync(admin, open.Id, Day.AddDays(1));
        var accepted = await OpenAsync(admin);
        await PutAsync(admin, accepted.Id, Entry("accepted", 0));
        await AcceptAsync(admin, accepted.Id);
        scope.SetUserWithDefaults(identifier: admin.Identifier);

        // Act
        await scope.SendAsync(new DeleteTenant.Command(tenant.Id));

        // Assert
        var rows = await new TestingServiceScope().ExecuteDbContextAsync(async db => (
            await db.ImportBatches.IgnoreQueryFilters().CountAsync(b => b.TenantId == tenant.Id),
            await db.ImportEntries.IgnoreQueryFilters().CountAsync(e => e.UserId == admin.Id),
            await db.ImportDays.IgnoreQueryFilters().CountAsync(d => d.UserId == admin.Id),
            await db.Sessions.IgnoreQueryFilters().CountAsync(s => s.UserId == admin.Id)));
        rows.ShouldBe((0, 0, 0, 0));
    }
}
