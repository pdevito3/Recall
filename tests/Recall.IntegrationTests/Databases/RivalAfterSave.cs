namespace Recall.IntegrationTests.Databases;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Runs a rival once, at the end of the first save whose changes match, before the request under test goes on. So a test
/// can put a rival between a save and the work that a request does after it. The save holds the writer line and its
/// transaction until the hook ends, so the rival must not use the database.
/// </summary>
internal sealed class RivalAfterSave : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, byte> _matched = new();
    private Func<DbContext, bool>? _matches;
    private Func<Task>? _rival;

    /// <param name="matches">Reads the changes of a save before it writes them.</param>
    /// <param name="rival">Runs at the end of the first save that matches.</param>
    public void Arm(Func<DbContext, bool> matches, Func<Task> rival)
    {
        _matches = matches;
        _rival = rival;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _matches is { } matches && _rival is not null && matches(context))
            _matched.TryAdd(context, 0);
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _matched.TryRemove(context, out _) && Interlocked.Exchange(ref _rival, null) is { } rival)
            await rival();
        return result;
    }
}
