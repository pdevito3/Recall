namespace Recall.UnitTests.Databases.Sqlite;

using Microsoft.Data.Sqlite;
using Shouldly;

public class SqliteEngineTests
{
    // SQLite through 3.51.2 can corrupt a WAL database when two connections write or checkpoint at the same instant.
    private static readonly Version FirstFixedEngine = new(3, 51, 3);

    [Test]
    public async Task the_engine_has_the_fix_for_the_wal_reset_bug()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        // Act
        var engine = Version.Parse((string)(await command.ExecuteScalarAsync())!);

        // Assert
        engine.ShouldBeGreaterThanOrEqualTo(FirstFixedEngine);
    }
}
