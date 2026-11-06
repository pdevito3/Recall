namespace Recall.Server.Databases.Sqlite;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Serilog;

public static class WalScrub
{
    private const string ShortWait = "PRAGMA busy_timeout = 250;";
    private const string Truncate = "PRAGMA wal_checkpoint(TRUNCATE);";
    private const int BusyColumn = 0;

    /// <summary>The delete has committed, so this takes no token. A client that goes away must not leave the words on the disk. It goes through the locks of SQLite, so another process that holds the WAL makes it say false.</summary>
    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
        Justification = "A checkpoint is outside each transaction of the context, so it uses a connection of its own. The SQL is a constant.")]
    public static async Task<bool> TryTruncateAsync(string connectionString)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var wait = connection.CreateCommand();
            wait.CommandText = ShortWait;
            await wait.ExecuteNonQueryAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = Truncate;
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetInt32(BusyColumn) == 0;
        }
        catch (SqliteException exception)
        {
            Log.ForContext(typeof(WalScrub)).Warning(exception, "Could not truncate the WAL");
            return false;
        }
    }
}
