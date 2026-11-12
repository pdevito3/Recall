namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Domain.UserSettings;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class FollowTheDeviceTimeZoneMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261111025258_AddTheAudioFileOfARecording";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public FollowTheDeviceTimeZoneMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments("Europe/Paris", true, "Fixed", "Europe/Paris")]
    [Arguments(null, true, "FollowDevice", "UTC")]
    [Arguments(null, false, null, "UTC")]
    public async Task up_fixes_a_stored_zone_and_gives_each_session_the_zone_of_its_user(
        string? storedZone, bool hasSettings, string? mode, string sessionZone)
    {
        // Arrange
        await MigrateAsync(target: null);
        var (userId, sessionId) = await AddUserWithSessionAsync(hasSettings);
        await MigrateAsync(MigrationBefore);
        await ExecuteAsync("UPDATE user_settings SET time_zone = $zone WHERE user_id = $id", ("$zone", storedZone), ("$id", userId));

        // Act
        await MigrateAsync(target: null);

        // Assert
        (await ScalarAsync("SELECT time_zone_mode FROM user_settings WHERE user_id = $id", userId)).ShouldBe(mode);
        (await ScalarAsync("SELECT time_zone FROM sessions WHERE id = $id", sessionId)).ShouldBe(sessionZone);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<(string UserId, string SessionId)> AddUserWithSessionAsync(bool hasSettings)
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner)
            .Under(tenant);
        var factory = _services.GetRequiredService<IServiceScopeFactory>();
        await using (var system = factory.CreateSystemScope())
        {
            var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Tenants.Add(tenant);
            dbContext.Users.Add(owner);
            await dbContext.SaveChangesAsync();
        }

        var session = Session.Create(tenant.Id, owner.Id, $"migration-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, null, TimeZoneId.Utc, DateTimeOffset.UnixEpoch);
        await using (var journal = factory.CreateJournalScope(new JournalOwner(owner.Id, owner.TenantId)))
        {
            var dbContext = journal.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Sessions.Add(session);
            if (hasSettings)
                dbContext.UserSettings.Add(UserSetting.DefaultsFor(tenant.Id, owner.Id));
            await dbContext.SaveChangesAsync();
        }

        return (owner.Id, session.Id);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql, string id)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync() as string;
    }
}
