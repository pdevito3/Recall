namespace Recall.IntegrationTests.Examples.Venues;

using Recall.IntegrationTests.Databases;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Examples.Venues.Features;
using Recall.Server.Exceptions;
using Recall.Server.Resources;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Examples.Fakes;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

/// <summary>
/// A venue add at the same time as a tenant delete must not leave a live venue in the deleted tenant. See
/// <c>docs/backend/concurrency.md</c>, section "Lock".
/// </summary>
public class VenueTenantDeleteRaceTests : TestBase
{
    [Test]
    public async Task venue_add_during_a_tenant_delete_waits_and_then_finds_no_tenant()
    {
        // Arrange
        var pause = PauseAtQuery.After("tenants.delete.load_venues");
        await using var services = pause.BuildServices();
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = UserRole.Admin().Value)
            .BuildAsync();
        var underTest = new TestingServiceScope(services);
        var rival = new TestingServiceScope();
        underTest.SetUserWithDefaults(identifier: admin.Identifier);
        rival.SetUserWithDefaults(identifier: admin.Identifier);

        // Act
        var delete = underTest.SendAsync(new DeleteTenant.Command(tenant.Id));
        await pause.ReachedAsync();
        var add = rival.SendAsync(new AddVenue.Command(new FakeVenueForCreationDto().Generate()));
        await pause.WaitUntilBlockedAsync(add);
        pause.Release();

        // Assert
        await delete;
        (await Should.ThrowAsync<NotFoundException>(add)).Message.ShouldBe("The tenant does not exist.");
        (await LiveVenueCountAsync(tenant.Id)).ShouldBe(0);
    }

    [Test]
    public async Task tenant_delete_during_a_venue_add_waits_and_then_deletes_the_new_venue()
    {
        // Arrange
        // EF tags no insert, so the hold matches its SQL. A save after the work of the lock holds no lock at the insert.
        var pause = PauseAtQuery.Before("INSERT INTO venues");
        await using var services = pause.BuildServices();
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = UserRole.Admin().Value)
            .BuildAsync();
        var underTest = new TestingServiceScope(services);
        var rival = new TestingServiceScope();
        underTest.SetUserWithDefaults(identifier: admin.Identifier);
        rival.SetUserWithDefaults(identifier: admin.Identifier);

        // Act
        var add = underTest.SendAsync(new AddVenue.Command(new FakeVenueForCreationDto().Generate()));
        await pause.ReachedAsync();
        var delete = rival.SendAsync(new DeleteTenant.Command(tenant.Id));
        await pause.WaitUntilBlockedAsync(delete);
        pause.Release();

        // Assert
        var venue = await add;
        await delete;
        (await new TestingServiceScope().ExecuteDbContextAsync(db =>
            db.Venues.IgnoreQueryFilters().Where(v => v.Id == venue.Id).Select(v => v.IsDeleted).SingleAsync())).ShouldBeTrue();
        (await LiveVenueCountAsync(tenant.Id)).ShouldBe(0);
    }

    private static Task<int> LiveVenueCountAsync(string tenantId)
        => new TestingServiceScope().ExecuteDbContextAsync(db =>
            db.Venues.IgnoreQueryFilters([QueryFilterNames.Tenant]).CountAsync(v => v.TenantId == tenantId));
}
