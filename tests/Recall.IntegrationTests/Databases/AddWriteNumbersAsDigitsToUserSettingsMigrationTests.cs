namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.UserSettings;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class AddWriteNumbersAsDigitsToUserSettingsMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261116085413_AddPersonalAccessTokens";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public AddWriteNumbersAsDigitsToUserSettingsMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "SES-52")]
    public async Task up_turns_the_number_format_on_for_each_user_from_before_the_setting()
    {
        // Arrange
        await MigrateAsync(target: null);
        var userId = await AddUserWithSettingsAsync();
        await MigrateAsync(MigrationBefore);

        // Act
        await MigrateAsync(target: null);

        // Assert
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT write_numbers_as_digits FROM user_settings WHERE user_id = $id", connection);
        command.Parameters.AddWithValue("$id", userId);
        (await command.ExecuteScalarAsync()).ShouldBe(1L);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<string> AddUserWithSettingsAsync()
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

        await using (var journal = factory.CreateJournalScope(new JournalOwner(owner.Id, owner.TenantId)))
        {
            var dbContext = journal.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.UserSettings.Add(UserSetting.DefaultsFor(tenant.Id, owner.Id));
            await dbContext.SaveChangesAsync();
        }

        return owner.Id;
    }
}
