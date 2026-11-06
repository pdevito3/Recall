namespace Recall.IntegrationTests.Databases;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.IntegrationTests.Auth;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

/// <summary>
/// An install keeps a live tenant, because with none only a change outside the API brings the login back. Each test has a database
/// of its own, because the shared database of the other tests always has the seed tenant. See <c>docs/backend/concurrency.md</c>.
/// </summary>
public sealed class LastTenantTests
{
    [Test]
    public async Task the_delete_of_the_last_tenant_is_refused_and_a_deleted_tenant_does_not_count()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var services = database.BuildServices();
        var deleted = await AddTenantAsync(services, "Deleted");
        var last = await AddTenantAsync(services, "Last");
        await DeleteAsync(services, deleted);

        // Act
        var act = () => DeleteAsync(services, last);

        // Assert
        (await Should.ThrowAsync<ConflictException>(act)).Message.ShouldBe(LastTenant.RefusedMessage);
        (await LiveTenantsAsync(database)).ShouldBe([last]);
    }

    [Test]
    public async Task the_delete_of_a_second_tenant_works_and_leaves_the_first()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var services = database.BuildServices();
        var first = await AddTenantAsync(services, "First");
        var second = await AddTenantAsync(services, "Second");

        // Act
        await DeleteAsync(services, second);

        // Assert
        (await LiveTenantsAsync(database)).ShouldBe([first]);
    }

    [Test]
    public async Task two_deletes_of_two_tenants_at_one_time_leave_one_tenant()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var plain = database.BuildServices();
        var first = await AddTenantAsync(plain, "First");
        var second = await AddTenantAsync(plain, "Second");
        var pause = PauseAtQuery.After("tenants.delete.another_live");
        var paused = database.BuildServices(interceptor: pause);

        // Act
        var deleteFirst = DeleteAsync(paused, first);
        await pause.ReachedAsync();
        var deleteSecond = DeleteAsync(plain, second);
        await pause.WaitUntilBlockedAsync(deleteSecond, () => RootLock.Install.HasWaiter(LastTenant.InstallRoot));
        pause.Release();

        // Assert
        await deleteFirst;
        (await Should.ThrowAsync<ConflictException>(deleteSecond)).Message.ShouldBe(LastTenant.RefusedMessage);
        (await LiveTenantsAsync(database)).ShouldBe([second]);
    }

    private static async Task<string> AddTenantAsync(IServiceProvider services, string name)
    {
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = Tenant.Create(new TenantForCreation { Name = name });
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task DeleteAsync(IServiceProvider services, string tenantId)
    {
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new DeleteTenant.Command(tenantId));
    }

    private static Task<List<string>> LiveTenantsAsync(AuthTestDatabase database)
        => database.ReadAsync(db => db.Tenants.Select(t => t.Id).ToListAsync());
}
