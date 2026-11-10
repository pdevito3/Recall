namespace Recall.Server.Databases.Sqlite;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Sets the options of SQLite that last only as long as a connection, each time EF opens one. A pooled connection also
/// comes through here. WAL stays in the file, so <see cref="SqliteFile"/> sets it once.
/// </summary>
public sealed class SqliteConnectionSetup : DbConnectionInterceptor
{
    private const string Pragmas =
        // FULL syncs the WAL at each commit, so a power cut loses no committed entry.
        // A WAL file keeps its largest size. The limit is 16 MiB, four times the 4 MiB between two default checkpoints,
        // so normal use does not truncate the file, and SQLite cuts it back at its first reset after a burst of writes.
        // SECURE_DELETE zeroes the bytes of a deleted row, so a deleted journal entry leaves the file at the next checkpoint.
        "PRAGMA synchronous = FULL; PRAGMA journal_size_limit = 16777216; PRAGMA secure_delete = ON;";

    public static SqliteConnectionSetup Instance { get; } = new();

    private SqliteConnectionSetup()
    {
    }

    /// <summary>
    /// The wait of SQLite for the write lock, in milliseconds: the <c>Default Timeout</c> of the connection string,
    /// 30 s when it has none. <see cref="SqliteWriterLine"/> gives the write of a turn only the rest of it.
    /// </summary>
    public static int BusyTimeoutOf(DbConnection connection) => ((SqliteConnection)connection).DefaultTimeout * 1000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmasOf(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmasOf(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string PragmasOf(DbConnection connection) => $"PRAGMA busy_timeout = {BusyTimeoutOf(connection)}; {Pragmas}";
}
