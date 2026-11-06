namespace Recall.Server.Databases;

using System.Diagnostics;
using Resources;

// The lock lives in this process and has no database state, which is correct because Recall runs one process per data file.
public sealed class RootLock
{
    public static readonly RootLock Tenant = new("tenants");

    private readonly string _area;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);

    private RootLock(string area) => _area = area;

    public Task<T> RunSharedAsync<T>(AppDbContext dbContext, string rootId, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
        => RunAsync(dbContext, rootId, Mode.Shared, work, cancellationToken);

    public Task<T> RunExclusiveAsync<T>(AppDbContext dbContext, string rootId, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
        => RunAsync(dbContext, rootId, Mode.Exclusive, work, cancellationToken);

    internal bool HasWaiter(string rootId)
    {
        lock (_gate)
            return _roots.TryGetValue(rootId, out var root) && root.Waiters.Count > 0;
    }

    private async Task<T> RunAsync<T>(AppDbContext dbContext, string rootId, Mode mode,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (dbContext.ChangeTracker.HasChanges() || dbContext.HasUnsavedActions)
            throw new InvalidOperationException(
                "Take the lock before the first change. A change made before the lock rests on reads that the lock did not protect.");
        if (dbContext.HoldsRootLock)
            throw new InvalidOperationException(
                "This request already holds a root lock. A second lock would wait for itself behind the next waiter.");

        var modeName = mode == Mode.Shared ? "shared" : "exclusive";
        Activity.Current?.SetTag("app.lock.mode", modeName);
        await EnterAsync(rootId, mode, modeName, cancellationToken);
        dbContext.HoldsRootLock = true;
        try
        {
            return await work(cancellationToken);
        }
        finally
        {
            dbContext.HoldsRootLock = false;
            Exit(rootId, mode);
        }
    }

    private async Task EnterAsync(string rootId, Mode mode, string modeName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootId, out var root))
                _roots[rootId] = root = new RootState();

            if (root.Waiters.Count == 0 && root.CanGrant(mode))
            {
                root.Grant(mode);
                return;
            }

            waiter = new Waiter(mode);
            root.Waiters.AddLast(waiter);
        }

        using var activity = Telemetry.Trace([("app.lock.mode", modeName)], $"{_area}.lock_wait");
        await using var _ = cancellationToken.Register(() => Abandon(rootId, waiter, cancellationToken));
        await waiter.Granted.Task;
    }

    private void Exit(string rootId, Mode mode)
    {
        lock (_gate)
        {
            var root = _roots[rootId];
            root.Release(mode);
            GrantWaiters(rootId, root);
        }
    }

    private void Abandon(string rootId, Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootId, out var root) || !root.Waiters.Remove(waiter))
                return;

            waiter.Granted.TrySetCanceled(cancellationToken);
            GrantWaiters(rootId, root);
        }
    }

    private void GrantWaiters(string rootId, RootState root)
    {
        while (root.Waiters.First is { } next && root.CanGrant(next.Value.Mode))
        {
            root.Waiters.RemoveFirst();
            root.Grant(next.Value.Mode);
            next.Value.Granted.TrySetResult();
        }

        if (root.IsIdle)
            _roots.Remove(rootId);
    }

    private enum Mode
    {
        Shared,
        Exclusive,
    }

    private sealed class Waiter(Mode mode)
    {
        public Mode Mode { get; } = mode;
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RootState
    {
        private int _sharedHolders;
        private bool _exclusiveHeld;

        public LinkedList<Waiter> Waiters { get; } = [];

        public bool IsIdle => _sharedHolders == 0 && !_exclusiveHeld && Waiters.Count == 0;

        public bool CanGrant(Mode mode)
            => !_exclusiveHeld && (mode == Mode.Shared || _sharedHolders == 0);

        public void Grant(Mode mode)
        {
            if (mode == Mode.Shared)
                _sharedHolders++;
            else
                _exclusiveHeld = true;
        }

        public void Release(Mode mode)
        {
            if (mode == Mode.Shared)
                _sharedHolders--;
            else
                _exclusiveHeld = false;
        }
    }
}
