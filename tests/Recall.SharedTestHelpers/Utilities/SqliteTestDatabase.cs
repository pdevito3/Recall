namespace Recall.SharedTestHelpers.Utilities;

using Microsoft.Data.Sqlite;

public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private const int DeleteAttempts = 10;

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

    public async ValueTask DisposeAsync()
    {
        using (var connection = new SqliteConnection(ConnectionString))
            SqliteConnection.ClearPool(connection);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            // The last metric export of a stopped host can make a file while the delete walks the folder.
            catch (IOException) when (attempt < DeleteAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }
}
