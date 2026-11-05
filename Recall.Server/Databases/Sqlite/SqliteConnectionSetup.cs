namespace Recall.Server.Databases.Sqlite;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Sets the options of SQLite that last only as long as a connection, each time EF opens one. A pooled connection also
/// comes through here. WAL stays in the file, so <see cref="SqliteFile"/> sets it once.
/// </summary>
public sealed class SqliteConnectionSetup : DbConnectionInterceptor
{
    private const string Pragmas =
        // A writer waits for the writer before it.
        // FULL syncs the WAL at each commit, so a power cut loses no committed entry.
        // A WAL file keeps its largest size. The limit is 16 MiB, four times the 4 MiB between two default checkpoints,
        // so normal use does not truncate the file, and SQLite cuts it back at its first reset after a burst of writes.
        "PRAGMA busy_timeout = 30000; PRAGMA synchronous = FULL; PRAGMA journal_size_limit = 16777216;";

    public static SqliteConnectionSetup Instance { get; } = new();

    private SqliteConnectionSetup()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
