namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
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
        var scrubbed = await WalScrub.TryTruncateAsync(ConnectionString);

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
        var scrubbed = await WalScrub.TryTruncateAsync(ConnectionString);

        // Assert
        scrubbed.ShouldBeFalse();
        new FileInfo(WalPath).Length.ShouldBeGreaterThan(0);
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_truncate_that_a_reader_blocked_passes_on_a_retry_after_the_reader_ends()
    {
        // Arrange
        await using var writer = await OpenAsync();
        await RunAsync(writer, "PRAGMA journal_mode = WAL; CREATE TABLE words (text TEXT NOT NULL); INSERT INTO words VALUES ('some words');");
        await using var reader = await OpenAsync();
        await RunAsync(reader, "BEGIN; SELECT count(*) FROM words;");
        await RunAsync(writer, "INSERT INTO words VALUES ('more words');");
        var time = new FakeTimeProvider();
        using var scrub = new WalScrub(time);
        await scrub.StartAsync(CancellationToken.None);
        (await scrub.TruncateAsync(ConnectionString)).ShouldBeFalse();
        await RunAsync(reader, "COMMIT;");

        // Act
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (new FileInfo(WalPath).Length > 0 && DateTime.UtcNow < deadline)
        {
            time.Advance(WalScrub.RetryInterval);
            await Task.Delay(50);
        }

        // Assert
        new FileInfo(WalPath).Length.ShouldBe(0);
        await scrub.StopAsync(CancellationToken.None);
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
