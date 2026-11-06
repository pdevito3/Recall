namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Shouldly;

public class RootLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task shared_holders_of_one_root_run_at_the_same_time()
    {
        // Arrange
        var root = NewRoot();
        var (first, second) = (new Held(), new Held());

        // Act
        var firstTask = Shared(root, first.WorkAsync);
        var secondTask = Shared(root, second.WorkAsync);

        // Assert
        await Task.WhenAll(first.Running, second.Running).WaitAsync(Timeout);
        first.Release();
        second.Release();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(Timeout);
    }

    [Test]
    public async Task an_exclusive_holder_waits_for_a_shared_holder_and_a_shared_holder_waits_for_it()
    {
        // Arrange
        var root = NewRoot();
        var holder = new Held();
        var shared = Shared(root, holder.WorkAsync);
        await holder.Running.WaitAsync(Timeout);

        // Act
        var exclusiveRan = false;
        var exclusive = Exclusive(root, _ => Task.FromResult(exclusiveRan = true));
        await WaitForWaiterAsync(root);

        // Assert
        exclusiveRan.ShouldBeFalse();
        holder.Release();
        await Task.WhenAll(shared, exclusive).WaitAsync(Timeout);
        exclusiveRan.ShouldBeTrue();
    }

    [Test]
    public async Task a_later_shared_request_does_not_pass_an_exclusive_waiter()
    {
        // Arrange
        var root = NewRoot();
        var order = new List<string>();
        var first = new Held();
        var firstTask = Shared(root, first.WorkAsync);
        await first.Running.WaitAsync(Timeout);
        var exclusive = Exclusive(root, _ => Record(order, "exclusive"));
        await WaitForWaiterAsync(root);

        // Act
        var late = Shared(root, _ => Record(order, "late shared"));
        late.IsCompleted.ShouldBeFalse();
        first.Release();
        await Task.WhenAll(firstTask, exclusive, late).WaitAsync(Timeout);

        // Assert
        order.ShouldBe(["exclusive", "late shared"]);
    }

    [Test]
    public async Task a_canceled_waiter_leaves_the_queue_and_the_waiters_behind_it_run()
    {
        // Arrange
        var root = NewRoot();
        var first = new Held();
        var firstTask = Shared(root, first.WorkAsync);
        await first.Running.WaitAsync(Timeout);
        using var cancel = new CancellationTokenSource();
        var canceled = Exclusive(root, _ => Task.FromResult(true), cancel.Token);
        await WaitForWaiterAsync(root);
        var behind = Shared(root, _ => Task.FromResult(true));

        // Act
        await cancel.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(canceled.WaitAsync(Timeout));
        (await behind.WaitAsync(Timeout)).ShouldBeTrue();
        first.Release();
        await firstTask.WaitAsync(Timeout);
    }

    [Test]
    public async Task a_failed_holder_releases_the_lock()
    {
        // Arrange
        var root = NewRoot();

        // Act
        await Should.ThrowAsync<InvalidCastException>(
            Exclusive<bool>(root, _ => throw new InvalidCastException()));

        // Assert
        (await Exclusive(root, _ => Task.FromResult(true)).WaitAsync(Timeout)).ShouldBeTrue();
    }

    [Test]
    public async Task different_roots_do_not_wait_for_each_other()
    {
        // Arrange
        var first = new Held();
        var firstTask = Exclusive(NewRoot(), first.WorkAsync);
        await first.Running.WaitAsync(Timeout);

        // Act & Assert
        (await Exclusive(NewRoot(), _ => Task.FromResult(true)).WaitAsync(Timeout)).ShouldBeTrue();
        first.Release();
        await firstTask.WaitAsync(Timeout);
    }

    [Test]
    public async Task the_install_lock_can_run_inside_the_lock_of_a_tenant_and_each_lock_is_free_after()
    {
        // Arrange
        await using var dbContext = new AppDbContextFactory().CreateDbContext([]);
        var (tenant, install) = (NewRoot(), NewRoot());

        // Act
        await RootLock.Tenant.RunExclusiveAsync(dbContext, tenant,
            _ => RootLock.Install.RunExclusiveAsync(dbContext, install, _ => Task.FromResult(true), CancellationToken.None),
            CancellationToken.None);
        var again = await RootLock.Install.RunExclusiveAsync(dbContext, install, _ => Task.FromResult(true), CancellationToken.None);

        // Assert
        again.ShouldBeTrue();
    }

    [Test]
    public async Task the_lock_of_a_tenant_inside_the_install_lock_is_refused()
    {
        // Arrange
        await using var dbContext = new AppDbContextFactory().CreateDbContext([]);
        var (tenant, install) = (NewRoot(), NewRoot());

        // Act
        var act = () => RootLock.Install.RunExclusiveAsync(dbContext, install,
            _ => RootLock.Tenant.RunExclusiveAsync(dbContext, tenant, _ => Task.FromResult(true), CancellationToken.None),
            CancellationToken.None);

        // Assert
        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldContain("already holds a root lock");
    }

    [Test]
    public async Task a_second_lock_in_one_request_is_refused()
    {
        // Arrange
        await using var dbContext = new AppDbContextFactory().CreateDbContext([]);
        var root = NewRoot();

        // Act
        var act = () => RootLock.Tenant.RunSharedAsync(dbContext, root,
            _ => RootLock.Tenant.RunSharedAsync(dbContext, root, _ => Task.FromResult(true), CancellationToken.None),
            CancellationToken.None);

        // Assert
        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldContain("already holds a root lock");
        (await Exclusive(root, _ => Task.FromResult(true)).WaitAsync(Timeout)).ShouldBeTrue();
    }

    private static string NewRoot() => Guid.NewGuid().ToString("N");

    private static Task<T> Shared<T>(string root, Func<CancellationToken, Task<T>> work)
        => RunAsync(root, work, (dbContext, id, w, ct) => RootLock.Tenant.RunSharedAsync(dbContext, id, w, ct), CancellationToken.None);

    private static Task<T> Exclusive<T>(string root, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        => RunAsync(root, work, (dbContext, id, w, ct) => RootLock.Tenant.RunExclusiveAsync(dbContext, id, w, ct), cancellationToken);

    private static async Task<T> RunAsync<T>(string root, Func<CancellationToken, Task<T>> work,
        Func<AppDbContext, string, Func<CancellationToken, Task<T>>, CancellationToken, Task<T>> run, CancellationToken cancellationToken)
    {
        await using var dbContext = new AppDbContextFactory().CreateDbContext([]);
        return await run(dbContext, root, work, cancellationToken);
    }

    private static Task<bool> Record(List<string> order, string name)
    {
        order.Add(name);
        return Task.FromResult(true);
    }

    private static async Task WaitForWaiterAsync(string root)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!RootLock.Tenant.HasWaiter(root))
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
    }

    private sealed class Held
    {
        private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Running => _running.Task;

        public void Release() => _released.SetResult();

        public async Task<bool> WorkAsync(CancellationToken cancellationToken)
        {
            _running.SetResult();
            await _released.Task;
            return true;
        }
    }
}
