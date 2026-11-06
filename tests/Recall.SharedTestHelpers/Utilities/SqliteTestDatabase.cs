namespace Recall.SharedTestHelpers.Utilities;

using Microsoft.Data.Sqlite;

public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall_test_{Guid.NewGuid():N}");

    public SqliteTestDatabase()
    {
        Directory.CreateDirectory(_directory);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "test.db") }.ConnectionString;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        command.ExecuteNonQuery();
    }

    public string ConnectionString { get; }

    public ValueTask DisposeAsync()
    {
        using (var connection = new SqliteConnection(ConnectionString))
            SqliteConnection.ClearPool(connection);

        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
