namespace Recall.Server.Databases.Sqlite;

using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

/// <summary>
/// One line of writers for each connection string. SQLite has no line for its write lock. A writer that finds the lock
/// taken sleeps and tries again, and its sleep grows to 100 ms, so a writer that just came tries far more often than one
/// that has waited for a long time. When the lock is seldom free, the oldest writer can lose each try until
/// <c>busy_timeout</c> ends, and it gets "database is locked". See docs/backend/concurrency.md.
/// </summary>
public sealed class SqliteWriterLine : DbCommandInterceptor
{
    private static readonly ConcurrentDictionary<string, Line> LinesByConnectionString = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<DbCommand, Turn> _turns = new();

    public static SqliteWriterLine Instance { get; } = new();

    private SqliteWriterLine()
    {
    }

    internal static async Task<Turn> WaitForTurnAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        var line = LinesByConnectionString.GetOrAdd(database.GetConnectionString() ?? "", _ => new Line(new SemaphoreSlim(1, 1)));
        var wait = line.Gate.WaitAsync(cancellationToken);
        // After the wait has its place, so a writer that a test counts cannot lose its place to a later writer.
        Interlocked.Increment(ref line.Writers);
        try
        {
            await wait;
        }
        catch
        {
            Interlocked.Decrement(ref line.Writers);
            throw;
        }

        return new Turn(line);
    }

    internal static int WritersInLineWithTurnHolder(string connectionString)
        => LinesByConnectionString.TryGetValue(connectionString, out var line) ? Volatile.Read(ref line.Writers) : 0;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // An open transaction holds the write lock or the turn of its save, so a wait here would not end.
        if (eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete
            && eventData.Context is { Database.CurrentTransaction: null } context)
            _turns[command] = await WaitForTurnAsync(context.Database, cancellationToken);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        EndTurn(command);
        return ValueTask.FromResult(result);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        EndTurn(command);
        return Task.CompletedTask;
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        EndTurn(command);
        return Task.CompletedTask;
    }

    private void EndTurn(DbCommand command)
    {
        if (_turns.TryRemove(command, out var turn))
            turn.Dispose();
    }

    internal readonly struct Turn(Line line) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref line.Writers);
            line.Gate.Release();
        }
    }

    internal sealed class Line(SemaphoreSlim gate)
    {
        public SemaphoreSlim Gate { get; } = gate;
        public int Writers;
    }
}
