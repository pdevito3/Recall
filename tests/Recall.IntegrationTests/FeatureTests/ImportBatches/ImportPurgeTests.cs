namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Microsoft.EntityFrameworkCore;
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
}
