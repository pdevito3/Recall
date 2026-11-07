namespace Recall.Server.Databases.Sqlite;

using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

/// <summary>
/// One line of writers for each data file in this process. SQLite has no line for its write lock. A writer that finds the
/// lock taken sleeps and tries again, and its sleep grows to 100 ms, so a writer that just came tries far more often than
/// one that has waited for a long time. When the lock is seldom free, the oldest writer can lose each try until
/// <c>busy_timeout</c> ends, and it gets "database is locked". It also holds a thread for all of that wait. In this line
/// the first writer goes first, and a writer that waits holds no thread. A save takes its turn in
/// <see cref="AppDbContext"/>, and this interceptor takes the turn of a write that does not go through a save.
/// <c>busy_timeout</c> still covers each writer outside this line: BackWave workers, a checkpoint, and another process.
/// </summary>
public sealed class SqliteWriterLine : DbCommandInterceptor
{
    private static readonly ConcurrentDictionary<string, Line> Lines = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<DbCommand, Turn> _turns = new();

    public static SqliteWriterLine Instance { get; } = new();

    private SqliteWriterLine()
    {
    }

    internal static async Task<Turn> WaitForTurnAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        var line = Lines.GetOrAdd(database.GetConnectionString() ?? "", _ => new Line(new SemaphoreSlim(int.MaxValue, int.MaxValue)));
        Interlocked.Increment(ref line.Writers);
        try
        {
            await line.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref line.Writers);
            throw;
        }

        return new Turn(line);
    }

    /// <summary>How many writers are in the line for this file. The writer that has the turn counts.</summary>
    internal static int WritersFor(string connectionString)
        => Lines.TryGetValue(connectionString, out var line) ? Volatile.Read(ref line.Writers) : 0;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // A write in a transaction of its caller holds the write lock, so it must not wait for a writer that waits for that lock.
        if (eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete or CommandSource.ExecuteSqlRaw
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
