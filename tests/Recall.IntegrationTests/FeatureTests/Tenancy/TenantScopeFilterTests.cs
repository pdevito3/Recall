namespace Recall.IntegrationTests.FeatureTests.Tenancy;

using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class TenantScopeFilterTests : TestBase
{
    [Test]
    public async Task user_with_no_user_record_sees_no_row_even_when_the_row_has_an_empty_tenant_id()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var user)
            .BuildAsync();

        await TestFixture.ExecuteOutOfBandAsync(
            "PRAGMA foreign_keys = OFF; UPDATE users SET tenant_id = @tenantId WHERE id = @id;",
            ("tenantId", string.Empty), ("id", user.Id));

        try
        {
            testingServiceScope.SetUserWithDefaults(identifier: $"unknown-{Guid.NewGuid()}");

            // Act
            var users = await testingServiceScope.ExecuteDbContextAsync(db => db.Users.ToListAsync());
            var found = await testingServiceScope.ExecuteDbContextAsync(db => db.Users.AnyAsync(u => u.Id == user.Id));

            // Assert
            users.ShouldBeEmpty();
            found.ShouldBeFalse();
        }
        finally
        {
            await TestFixture.ExecuteOutOfBandAsync("DELETE FROM users WHERE id = @id", ("id", user.Id));
        }
    }
}
