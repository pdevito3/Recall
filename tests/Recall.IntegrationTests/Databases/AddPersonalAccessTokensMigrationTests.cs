namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class AddPersonalAccessTokensMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261115124342_AddRemoveFillerWordsToUserSettings";
    private const string At = "2026-03-01T12:00:00.000000Z";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public AddPersonalAccessTokensMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "PAT-1")]
    public async Task the_rebuild_of_auth_sessions_keeps_each_row_the_index_and_the_checks()
    {
        // Arrange
        await MigrateAsync(MigrationBefore);
        var userId = await AddUserAsync();
        await ExecuteAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, last_slid_at)
            VALUES ('ses_web', '{userId}', 'web', 'oidc', 'hash-web', '{At}', '{At}', '{At}', '{At}');
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, refresh_family_hash, refresh_hash, expires_at, created_at, revoked_at, revoked_reason)
            VALUES ('ses_native', '{userId}', 'native', 'oidc', 'hash-native', '{At}', 'family', 'refresh', '{At}', '{At}', '{At}', 'logout');
            """);
        var rowsBefore = await RowsAsync();

        // Act
        await MigrateAsync(target: null);

        // Assert
        rowsBefore.Count.ShouldBe(2);
        (await RowsAsync()).ShouldBe(rowsBefore);
        (await ScalarAsync("SELECT sql FROM sqlite_master WHERE name = 'ux_auth_sessions_access_hash'"))
            .ShouldBe("CREATE UNIQUE INDEX \"ux_auth_sessions_access_hash\" ON \"auth_sessions\" (\"access_hash\")");
        await ExecuteAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name, "grant")
            VALUES ('pat_ok', '{userId}', 'pat', 'pat', 'hash-pat', '{At}', '{At}', '{At}', 'Laptop', 'read:');
            """);
        (await ScalarAsync("SELECT count(*) FROM auth_sessions WHERE id = 'pat_ok'")).ShouldBe(1L);
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name)
            VALUES ('pat_no_grant', '{userId}', 'pat', 'pat', 'hash-no-grant', '{At}', '{At}', '{At}', 'Laptop');
            """, "ck_auth_sessions_grant");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, "grant")
            VALUES ('pat_no_name', '{userId}', 'pat', 'pat', 'hash-no-name', '{At}', '{At}', '{At}', 'read:');
            """, "ck_auth_sessions_name");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name, "grant", refresh_family_hash, refresh_hash)
            VALUES ('pat_refresh', '{userId}', 'pat', 'pat', 'hash-refresh', '{At}', '{At}', '{At}', 'Laptop', 'read:', 'f', 'r');
            """, "ck_auth_sessions_refresh_state");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name)
            VALUES ('ses_named', '{userId}', 'web', 'oidc', 'hash-named', '{At}', '{At}', '{At}', 'Laptop');
            """, "ck_auth_sessions_name");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, last_used_at)
            VALUES ('ses_used', '{userId}', 'web', 'oidc', 'hash-used', '{At}', '{At}', '{At}', '{At}');
            """, "ck_auth_sessions_last_used_at");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at)
            VALUES ('ses_native_no_refresh', '{userId}', 'native', 'oidc', 'hash-native-2', '{At}', '{At}', '{At}');
            """, "ck_auth_sessions_refresh_state");
        await RefusedAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at)
            VALUES ('ses_dup', '{userId}', 'web', 'oidc', 'hash-web', '{At}', '{At}', '{At}');
            """, "UNIQUE constraint failed: auth_sessions.access_hash");
    }

    [Test]
    public async Task the_down_deletes_each_personal_access_token_and_keeps_each_session()
    {
        // Arrange
        await MigrateAsync(target: null);
        var userId = await AddUserAsync();
        await ExecuteAsync($"""
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, last_slid_at)
            VALUES ('ses_web', '{userId}', 'web', 'oidc', 'hash-web', '{At}', '{At}', '{At}', '{At}');
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name, "grant")
            VALUES ('pat_ok', '{userId}', 'pat', 'pat', 'hash-pat', '{At}', '{At}', '{At}', 'Laptop', 'read:');
            """);

        // Act
        await MigrateAsync(MigrationBefore);

        // Assert
        (await ScalarAsync("SELECT group_concat(id) FROM auth_sessions")).ShouldBe("ses_web");
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<string> AddUserAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var user)
            .Under(tenant);
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var foreignKeys = new SqliteCommand("PRAGMA foreign_keys = ON", connection);
        await foreignKeys.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task RefusedAsync(string sql, string constraint)
    {
        var exception = await Should.ThrowAsync<SqliteException>(() => ExecuteAsync(sql));
        exception.Message.ShouldContain(constraint);
    }

    private async Task<List<string>> RowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqliteCommand(
            "SELECT id, user_id, client_kind, login_mode, access_hash, access_expires_at, refresh_family_hash, refresh_hash, "
            + "expires_at, created_at, last_slid_at, revoked_at, revoked_reason FROM auth_sessions ORDER BY id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : reader.GetString(i))));
        return rows;
    }
}
