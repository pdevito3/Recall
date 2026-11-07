namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class MigrationRoundTripTests : IAsyncDisposable
{
    // The last migration of main before the R9 migrations that come after it. EF rebuilds a table with the model of the
    // migration before, and a rebuild drops the triggers of a journal table (ADR 0020).
    private const string MigrationBefore = "20261107015110_AddWasDraftToRevisions";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public MigrationRoundTripTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    public async Task down_to_the_last_migration_of_main_and_up_again_gives_the_same_schema_with_each_trigger()
    {
        // Arrange
        await MigrateAsync(target: null);
        var before = await SchemaAsync();

        // Act
        await MigrateAsync(MigrationBefore);
        await MigrateAsync(target: null);

        // Assert
        before.ShouldContain(row => row.StartsWith("trigger|users_purge_journal|"));
        (await SchemaAsync()).ShouldBe(before);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<List<string>> SchemaAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{(reader.IsDBNull(3) ? "" : reader.GetString(3))}");
        }

        return rows;
    }
}
