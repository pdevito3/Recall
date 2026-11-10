namespace Recall.Server.Databases.Sqlite;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Resources;
using Serilog;
using SQLitePCL;

/// <summary>
/// One line of writers for each data file. SQLite has no line for its write lock. A writer that finds the lock
/// taken sleeps and tries again, and its sleep grows to 100 ms, so a writer that just came tries far more often than one
/// that has waited for a long time. When the lock is seldom free, the oldest writer can lose each try until
/// <c>busy_timeout</c> ends, and it gets "database is locked". A write has one limit in all, the
/// <c>Default Timeout</c> of its connection string: the wait in the line and the wait for the lock share it. See
/// docs/backend/concurrency.md.
/// </summary>
public sealed class SqliteWriterLine : DbCommandInterceptor
{
    private static readonly ConcurrentDictionary<string, Line> LinesByFile = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<DbCommand, Turn> _turns = new();

    public static SqliteWriterLine Instance { get; } = new();

    private SqliteWriterLine()
    {
    }

    /// <summary>
    /// Waits for the turn of the data file, then opens the connection and gives its wait for the SQLite lock only the
    /// rest of the limit. Dispose the turn to close the connection and let the next writer go.
    /// </summary>
    /// <exception cref="DatabaseBusyException">The limit ended in the line.</exception>
    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
        Justification = "The turn sets the lock wait of the connection and runs no command on it.")]
    internal static async Task<Turn> WaitForTurnAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        var options = new SqliteConnectionStringBuilder(database.GetConnectionString());
        var line = LinesByFile.GetOrAdd(FileOf(options), _ => new Line());
        var limit = TimeSpan.FromSeconds(options.DefaultTimeout);
        var asked = Stopwatch.GetTimestamp();
        var wait = line.Gate.WaitAsync(limit, cancellationToken);
        // After the wait has its place, so a writer that a test counts cannot lose its place to a later writer.
        Interlocked.Increment(ref line.Writers);
        bool entered;
        try
        {
            entered = wait.IsCompleted ? await wait : await WaitInLineAsync(line, limit, wait);
        }
        catch
        {
            Interlocked.Decrement(ref line.Writers);
            throw;
        }

        if (!entered)
        {
            Interlocked.Decrement(ref line.Writers);
            throw new DatabaseBusyException();
        }

        Volatile.Write(ref line.TurnStarted, Stopwatch.GetTimestamp());
        var turn = new Turn(line, database);
        try
        {
            await database.OpenConnectionAsync(cancellationToken);
            turn.LimitLockWait((SqliteConnection)database.GetDbConnection(), limit - Stopwatch.GetElapsedTime(asked));
            return turn;
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    internal static int WritersInLineWithTurnHolder(string connectionString)
        => LinesByFile.TryGetValue(FileOf(new SqliteConnectionStringBuilder(connectionString)), out var line)
            ? Volatile.Read(ref line.Writers)
            : 0;

    // One file can have many connection strings, such as a relative path, a path with ./, or other options.
    private static string FileOf(SqliteConnectionStringBuilder options) => Path.GetFullPath(options.DataSource);

    private static async Task<bool> WaitInLineAsync(Line line, TimeSpan limit, Task<bool> wait)
    {
        using var activity = Telemetry.Trace([("app.sqlite.writers", Volatile.Read(ref line.Writers))], "sqlite.writer_line_wait");
        if (await wait)
            return true;

        var held = Stopwatch.GetElapsedTime(Volatile.Read(ref line.TurnStarted));
        activity?.SetTag("app.sqlite.turn_held_ms", (long)held.TotalMilliseconds);
        Telemetry.MarkFailed(activity, new DatabaseBusyException());
        Log.ForContext<SqliteWriterLine>().Here().Warning(
            "A write gave up after {LimitSeconds} s in the writer line. The writer that has the turn has held it for {HeldSeconds} s, and {Writers} writers wait. A turn that does not end can mean a disk that hangs",
            limit.TotalSeconds, Math.Round(held.TotalSeconds, 1), Volatile.Read(ref line.Writers) - 1);
        return false;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
        => TakesATurn(eventData)
            ? throw new InvalidOperationException(
                "A sync ExecuteUpdate or ExecuteDelete does not wait in the writer line. Call ExecuteUpdateAsync or ExecuteDeleteAsync.")
            : result;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (TakesATurn(eventData))
        {
            var turn = await WaitForTurnAsync(eventData.Context!.Database, cancellationToken);
            command.CommandTimeout = ((SqliteConnection)command.Connection!).DefaultTimeout;
            _turns[command] = turn;
        }

        return result;
    }

    // An open transaction holds the write lock or the turn of its save, so a wait here would not end.
    private static bool TakesATurn(CommandEventData eventData)
        => eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete
           && eventData.Context is { Database.CurrentTransaction: null };

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

    internal sealed class Turn(Line line, DatabaseFacade database) : IDisposable
    {
        private SqliteConnection? _limited;
        private int _defaultTimeout;

        // busy_timeout bounds the wait of SQLite inside one step. Microsoft.Data.Sqlite then steps again until the
        // timeout of the command ends, so both get the rest of the limit.
        public void LimitLockWait(SqliteConnection connection, TimeSpan left)
        {
            var milliseconds = Math.Max(1, (int)left.TotalMilliseconds);
            _defaultTimeout = connection.DefaultTimeout;
            _limited = connection;
            connection.DefaultTimeout = Math.Max(1, milliseconds / 1000);
            raw.sqlite3_busy_timeout(connection.Handle, milliseconds);
        }

        public void Dispose()
        {
            if (_limited is { } connection)
            {
                connection.DefaultTimeout = _defaultTimeout;
                if (connection.Handle is { } handle)
                    raw.sqlite3_busy_timeout(handle, SqliteConnectionSetup.BusyTimeoutOf(connection));
                database.CloseConnection();
            }

            Interlocked.Decrement(ref line.Writers);
            line.Gate.Release();
        }
    }

    internal sealed class Line
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Writers;
        public long TurnStarted;
    }
}
