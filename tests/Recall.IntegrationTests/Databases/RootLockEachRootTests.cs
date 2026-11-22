namespace Recall.IntegrationTests.Databases;

using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Shouldly;

/// <summary>A delete of a login provider holds the lock of each live tenant, so a guard of one tenant waits for it (IDP-14).</summary>
public sealed class RootLockEachRootTests
{
    [Test]
    public async Task a_holder_of_one_root_waits_for_a_holder_of_each_root_and_then_runs()
    {
        // Arrange
        using var first = TestFixture.BaseScopeFactory.CreateScope();
        using var second = TestFixture.BaseScopeFactory.CreateScope();
        var (a, b) = ($"ten_lock_a_{Guid.NewGuid():N}", $"ten_lock_b_{Guid.NewGuid():N}");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        // Act
        var each = RootLock.Tenant.RunExclusiveAsync(first.ServiceProvider.GetRequiredService<AppDbContext>(), [b, a], async _ =>
        {
            await release.Task;
            lock (order)
                order.Add("each");
            return true;
        }, CancellationToken.None);
        var one = RootLock.Tenant.RunExclusiveAsync(second.ServiceProvider.GetRequiredService<AppDbContext>(), b, _ =>
        {
            lock (order)
                order.Add("one");
            return Task.FromResult(true);
        }, CancellationToken.None);
        for (var i = 0; i < 200 && !RootLock.Tenant.HasWaiter(b); i++)
            await Task.Delay(10);
        var waited = RootLock.Tenant.HasWaiter(b);
        release.SetResult();
        await Task.WhenAll(each, one);

        // Assert
        waited.ShouldBeTrue();
        order.ShouldBe(["each", "one"]);
        RootLock.Tenant.HasWaiter(a).ShouldBeFalse();
    }
}
