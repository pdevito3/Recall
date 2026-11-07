namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class RemoveTheClockFromDraftSavesMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261107015110_AddWasDraftToRevisions";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public RemoveTheClockFromDraftSavesMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "SES-7")]
    [Arguments("raw_revisions")]
    [Arguments("cleaned_revisions")]
    public async Task down_gives_each_revision_a_server_time_that_the_older_code_reads_and_keeps_the_time_of_the_client(string table)
    {
        // Arrange
        await MigrateAsync(target: null);
        var owner = await AddOwnerAsync();
        var clientTime = new DateTimeOffset(2026, 11, 6, 7, 8, 9, TimeSpan.Zero);
        await SaveBothDraftsAsync(owner, clientTime);

        // Act
        await MigrateAsync(MigrationBefore);

        // Assert
        var (serverText, clientText) = await TimesOfTheRevisionAsync(table);
        UtcTextConverter.FromText(serverText).ShouldBe(DateTimeOffset.UnixEpoch);
        UtcTextConverter.FromText(clientText).ShouldBe(clientTime);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<User> AddOwnerAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner)
            .Under(tenant);
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(owner);
        await dbContext.SaveChangesAsync();
        return owner;
    }

    private async Task SaveBothDraftsAsync(User owner, DateTimeOffset clientTime)
    {
        var scope = new TestingServiceScope(_services);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        var created = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = clientTime.AddMinutes(-5),
        }));
        var save = new DraftSaveDto { Text = "words", BaseRevision = 0, SavedAt = clientTime };
        await scope.SendAsync(new SaveRawDraft.Command(created.Session.Id, save));
        await scope.SendAsync(new SaveCleanedDraft.Command(created.Session.Id, save));
    }

    private async Task<(string Server, string Client)> TimesOfTheRevisionAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand($"SELECT saved_at, claimed_saved_at FROM {table}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.GetString(0), reader.GetString(1));
    }
}
