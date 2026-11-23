namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.TimeZones;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class ListEachMatchOfAPeopleProposalMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261123063717_AddPersonDetails";
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public ListEachMatchOfAPeopleProposalMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task up_turns_each_stored_match_into_a_list_of_matches()
    {
        // Arrange
        await MigrateAsync(target: null);
        var (owner, sessionId) = await AddSessionAsync();
        await MigrateAsync(MigrationBefore);
        await ExecuteAsync(
            "UPDATE sessions SET people_proposals = $proposals WHERE id = $id",
            ("$proposals", """[{"label":"Jane","matchedPersonId":"per_jane"},{"label":"Kim","matchedPersonId":null}]"""),
            ("$id", sessionId));

        // Act
        await MigrateAsync(target: null);

        // Assert
        await using var journal = _services.GetRequiredService<IServiceScopeFactory>().CreateJournalScope(owner);
        var session = await journal.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .TagWithOperationCallSite("tests.migration.read")
            .SingleAsync(s => s.Id == sessionId);
        session.PeopleProposals.ShouldBe([new PersonProposal("Jane", ["per_jane"]), new PersonProposal("Kim", [])]);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private async Task<(JournalOwner Owner, string SessionId)> AddSessionAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var user)
            .Under(tenant);
        var factory = _services.GetRequiredService<IServiceScopeFactory>();
        await using (var system = factory.CreateSystemScope())
        {
            var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Tenants.Add(tenant);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();
        }

        var owner = new JournalOwner(user.Id, user.TenantId);
        var session = Session.Create(tenant.Id, user.Id, "device-1", Noon, null, TimeZoneId.Utc, Noon);
        await using (var journal = factory.CreateJournalScope(owner))
        {
            var dbContext = journal.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Sessions.Add(session);
            await dbContext.SaveChangesAsync();
        }

        return (owner, session.Id);
    }
}
