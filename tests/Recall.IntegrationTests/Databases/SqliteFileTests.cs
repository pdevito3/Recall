namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

public sealed class SqliteFileTests
{
    [Test]
    public async Task prepare_makes_the_directory_and_sets_wal()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"recall-file-{Guid.NewGuid():N}", "nested", "recall.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

        try
        {
            // Act
            await SqliteFile.PrepareAsync(connectionString, CancellationToken.None);

            // Assert
            File.Exists(path).ShouldBeTrue();
            (await PragmaAsync(connectionString, "journal_mode")).ShouldBe("wal");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Test]
    public async Task prepare_removes_the_migration_lock_of_a_killed_process_and_migrate_then_ends()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        await ExecuteAsync(database.ConnectionString, """
            CREATE TABLE __EFMigrationsLock (Id INTEGER NOT NULL PRIMARY KEY, Timestamp TEXT NOT NULL);
            INSERT INTO __EFMigrationsLock VALUES (1, '2026-01-01');
            """);

        // Act
        await SqliteFile.PrepareAsync(database.ConnectionString, CancellationToken.None);
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(60));

        // Assert
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task a_connection_of_the_app_has_the_options_of_the_data_file()
    {
        // Arrange
        await using var connection = await OpenAppConnectionAsync();

        // Act
        var options = new Dictionary<string, string?>();
        foreach (var name in new[] { "busy_timeout", "synchronous", "foreign_keys", "journal_mode", "journal_size_limit" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {name}";
            options[name] = (await command.ExecuteScalarAsync())?.ToString();
        }

        // Assert
        options["busy_timeout"].ShouldBe("30000");
        options["synchronous"].ShouldBe("2");
        options["foreign_keys"].ShouldBe("1");
        options["journal_mode"].ShouldBe("wal");
        options["journal_size_limit"].ShouldBe("16777216");
    }

    [Test]
    public async Task a_wal_that_a_burst_of_writes_grew_is_cut_back_at_its_next_reset()
    {
        // Arrange
        const long limit = 16 * 1024 * 1024;
        await using var database = new SqliteTestDatabase();
        var options = new DbContextOptionsBuilder().UseSqlite(database.ConnectionString)
            .AddInterceptors(SqliteConnectionSetup.Instance).Options;
        await using var context = new DbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE burst (bytes BLOB)");
        await context.Database.ExecuteSqlRawAsync("INSERT INTO burst VALUES (randomblob(33554432))");
        var walPath = new SqliteConnectionStringBuilder(database.ConnectionString).DataSource + "-wal";
        new FileInfo(walPath).Length.ShouldBeGreaterThan(limit);

        // Act
        await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE)");
        await context.Database.ExecuteSqlRawAsync("INSERT INTO burst VALUES (x'00')");

        // Assert
        new FileInfo(walPath).Length.ShouldBeLessThanOrEqualTo(limit);
    }

    [Test]
    public async Task a_row_with_a_missing_parent_is_refused()
    {
        // Arrange
        await using var connection = await OpenAppConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, role, is_deleted, version) " +
            $"VALUES ('usr_{Guid.NewGuid():N}', 'ten_missing', 'A', 'B', '{Guid.NewGuid():N}', '{Guid.NewGuid():N}', 'e@example.com', 'User', 0, 1)";

        // Act
        var act = () => command.ExecuteNonQueryAsync();

        // Assert
        (await Should.ThrowAsync<SqliteException>(act)).SqliteExtendedErrorCode.ShouldBe(787);
    }

    private static async Task<DbConnection> OpenAppConnectionAsync()
    {
        var context = new TestingServiceScope().GetService<AppDbContext>();
        await context.Database.OpenConnectionAsync();
        return context.Database.GetDbConnection();
    }

    private static AppDbContext CreateContext(SqliteTestDatabase database)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, TimeProvider.System, Substitute.For<ICurrentUserService>(),
            Substitute.For<ICallerIdentityResolver>(), Substitute.For<ITenantScopeProvider>(),
            new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> PragmaAsync(string connectionString, string name)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand($"PRAGMA {name}", connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
