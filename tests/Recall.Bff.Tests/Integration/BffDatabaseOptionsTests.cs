namespace Recall.Bff.Tests.Integration;

using Recall.Bff.Sessions;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public sealed class BffDatabaseOptionsTests
{
    [Test]
    public async Task a_connection_of_the_bff_limits_the_size_of_its_wal()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var builder = new DbContextOptionsBuilder();
        BffSessionExtensions.ConfigureBffDb(builder, database.ConnectionString);
        await using var context = new DbContext(builder.Options);
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_size_limit";

        // Act
        var limit = await command.ExecuteScalarAsync();

        // Assert
        limit.ShouldBe(16777216L);
    }
}
