namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Recall.Server.Databases.Sqlite;
using Shouldly;

public class WalScrubTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wal-scrub-{Guid.NewGuid():N}.db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    private string WalPath => $"{_path}-wal";

    [After(Test)]
    public void DeleteTheFiles()
    {
        File.Delete(_path);
        File.Delete(WalPath);
        File.Delete($"{_path}-shm");
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_truncate_with_no_reader_empties_the_wal_and_says_so()
    {
        // Arrange
        await using var writer = await OpenAsync();
        await RunAsync(writer, "PRAGMA journal_mode = WAL; CREATE TABLE words (text TEXT NOT NULL); INSERT INTO words VALUES ('some words');");
        new FileInfo(WalPath).Length.ShouldBeGreaterThan(0);

        // Act
        var scrubbed = await WalScrub.TryTruncateAsync(ConnectionString, CancellationToken.None);

        // Assert
        scrubbed.ShouldBeTrue();
        new FileInfo(WalPath).Length.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_truncate_that_a_reader_blocks_says_so()
    {
        // Arrange
        await using var writer = await OpenAsync();
        await RunAsync(writer, "PRAGMA journal_mode = WAL; CREATE TABLE words (text TEXT NOT NULL); INSERT INTO words VALUES ('some words');");
        await using var reader = await OpenAsync();
        await RunAsync(reader, "BEGIN; SELECT count(*) FROM words;");
        await RunAsync(writer, "INSERT INTO words VALUES ('more words');");

        // Act
        var scrubbed = await WalScrub.TryTruncateAsync(ConnectionString, CancellationToken.None);

        // Assert
        scrubbed.ShouldBeFalse();
        new FileInfo(WalPath).Length.ShouldBeGreaterThan(0);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RunAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
