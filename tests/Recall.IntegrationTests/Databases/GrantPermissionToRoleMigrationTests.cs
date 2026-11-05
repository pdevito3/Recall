namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;

public sealed class GrantPermissionToRoleMigrationTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Test]
    public async Task grant_on_the_latest_schema_gives_the_permission_to_each_live_user_with_the_role_once()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(ArrangeSql);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.GrantPermissionToRole("Admin", "manage_agents");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
        {
            await ExecuteAsync(sql);
            await ExecuteAsync(sql);
        }

        // Assert
        var granted = await UsersWithAsync("manage_agents");
        granted.ShouldBe(["usr_admin"]);
    }

    [Test]
    public async Task grant_to_holders_gives_the_permission_to_each_live_holder_once_and_a_new_id()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(ArrangeSql);
        await ExecuteAsync("""
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version) VALUES
                ('uperm_held', 'usr_member', 'read_users', false, 1),
                ('uperm_soft_deleted', 'usr_admin', 'read_users', true, 1);
            """);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.GrantPermissionToHoldersOf("read_agents", "read_users");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
        {
            await ExecuteAsync(sql);
            await ExecuteAsync(sql);
        }

        // Assert
        (await UsersWithAsync("read_agents")).ShouldBe(["usr_member"]);
        var id = (await ScalarAsync("SELECT id FROM user_permissions WHERE permission = 'read_agents'")).ShouldBeOfType<string>();
        EntityId.IsValid(id, "uperm").ShouldBeTrue(id);
        Guid.Parse(id["uperm_".Length..]).Version.ShouldBe(7);
        Guid.Parse(id["uperm_".Length..]).Variant.ShouldBeInRange(8, 11);
    }

    [Test]
    public async Task delete_permission_removes_each_row_of_the_permission_also_a_soft_deleted_row()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(ArrangeSql);
        await ExecuteAsync("""
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version) VALUES
                ('uperm_gone', 'usr_member', 'old_permission', false, 1),
                ('uperm_gone_too', 'usr_admin', 'old_permission', true, 1),
                ('uperm_kept', 'usr_admin', 'read_users', false, 1);
            """);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.DeletePermission("old_permission");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
            await ExecuteAsync(sql);

        // Assert
        (await ScalarAsync("SELECT count(*) FROM user_permissions WHERE permission = 'old_permission'")).ShouldBe(0L);
        (await ScalarAsync("SELECT count(*) FROM user_permissions WHERE permission = 'read_users'")).ShouldBe(1L);
    }

    private const string ArrangeSql = """
        INSERT INTO tenants (id, name, is_deleted, version) VALUES ('ten_migration', 'Migration tenant', false, 1);
        INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, role, is_deleted, version) VALUES
            ('usr_admin', 'ten_migration', 'Ada', 'Admin', 'idp-admin', 'ada', 'ada@example.com', 'Admin', false, 1),
            ('usr_member', 'ten_migration', 'Mia', 'Member', 'idp-member', 'mia', 'mia@example.com', 'User', false, 1),
            ('usr_deletedadmin', 'ten_migration', 'Dan', 'Deleted', 'idp-deletedadmin', 'dan', 'dan@example.com', 'Admin', true, 1);
        """;

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, TimeProvider.System, Substitute.For<ICurrentUserService>(),
            Substitute.For<ICallerIdentityResolver>(), Substitute.For<ITenantScopeProvider>(), new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> UsersWithAsync(string permission)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            "SELECT user_id FROM user_permissions WHERE permission = @permission AND NOT is_deleted", connection);
        command.Parameters.AddWithValue("permission", permission);
        await using var reader = await command.ExecuteReaderAsync();
        var userIds = new List<string>();
        while (await reader.ReadAsync())
            userIds.Add(reader.GetString(0));
        return userIds;
    }
}
