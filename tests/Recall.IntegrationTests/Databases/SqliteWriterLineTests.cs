namespace Recall.IntegrationTests.Databases;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class SqliteWriterLineTests
{
    private const int WaitingWriters = 20;
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    [Test]
    public async Task writers_that_wait_for_the_write_lock_commit_in_the_order_that_they_came()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        var hold = new HoldInTheTransaction();
        var first = SaveTenantAsync(database, "first", hold);
        await hold.Reached.Task.WaitAsync(Limit);
        var names = Enumerable.Range(0, WaitingWriters).Select(number => $"writer-{number:D2}").ToList();
        var saves = new List<Task>();

        // Act
        foreach (var name in names)
        {
            saves.Add(SaveTenantAsync(database, name));
            await WaitForWritersAsync(database, saves.Count + 1);
        }
        hold.Release.SetResult();
        await Task.WhenAll(saves.Append(first)).WaitAsync(Limit);

        // Assert
        (await TenantNamesInCommitOrderAsync(database)).ShouldBe(names.Prepend("first").ToList());
    }

    [Test]
    public async Task a_writer_that_gives_up_leaves_the_line_and_the_next_writer_goes_on()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        var hold = new HoldInTheTransaction();
        var first = SaveTenantAsync(database, "first", hold);
        await hold.Reached.Task.WaitAsync(Limit);
        using var giveUp = new CancellationTokenSource();
        var quitter = SaveTenantAsync(database, "quitter", cancellationToken: giveUp.Token);
        await WaitForWritersAsync(database, 2);
        var next = SaveTenantAsync(database, "next");
        await WaitForWritersAsync(database, 3);

        // Act
        await giveUp.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => quitter.WaitAsync(Limit));
        hold.Release.SetResult();
        await Task.WhenAll(first, next).WaitAsync(Limit);

        // Assert
        (await TenantNamesInCommitOrderAsync(database)).ShouldBe(["first", "next"]);
    }

    [Test]
    public async Task a_write_that_is_not_a_save_waits_in_the_same_line()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        var hold = new HoldInTheTransaction();
        var first = SaveTenantAsync(database, "first", hold);
        await hold.Reached.Task.WaitAsync(Limit);
        await using var db = CreateContext(database);

        // Act
        var delete = Task.Run(() => db.Set<LoginAttempt>().ExecuteDeleteAsync());
        await WaitForWritersAsync(database, 2);
        hold.Release.SetResult();

        // Assert
        await Task.WhenAll(first, delete).WaitAsync(Limit);
        SqliteWriterLine.WritersInLineWithTurnHolder(database.ConnectionString).ShouldBe(0);
    }

    [Test]
    public async Task a_write_that_fails_ends_its_turn()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        await SeedLoginAttemptAsync(database);
        await using var db = CreateContext(database);

        // Act
        await Should.ThrowAsync<SqliteException>(() => db.Set<LoginAttempt>()
            .ExecuteUpdateAsync(set => set.SetProperty(attempt => attempt.Nonce, attempt => null!)).WaitAsync(Limit));

        // Assert
        await TheLineLetsOneWriterGoAtATimeAsync(database);
    }

    [Test]
    public async Task a_write_that_the_guard_refuses_leaves_the_line_working()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        await using var db = CreateContext(database);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => db.Tenants.IgnoreQueryFilters()
            .ExecuteUpdateAsync(set => set.SetProperty(tenant => tenant.Name, "Changed")).WaitAsync(Limit));

        // Assert
        await TheLineLetsOneWriterGoAtATimeAsync(database);
    }

    [Test]
    public async Task a_write_in_a_transaction_of_its_caller_does_not_wait_for_a_writer_that_waits_for_it()
    {
        // Arrange
        await using var database = await MigratedDatabaseAsync();
        await using var db = CreateContext(database);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var other = SaveTenantAsync(database, "other");
        await WaitForWritersAsync(database, 1);

        // Act
        await db.Set<LoginAttempt>().ExecuteDeleteAsync().WaitAsync(Limit);
        await transaction.CommitAsync();

        // Assert
        await other.WaitAsync(Limit);
        (await TenantNamesInCommitOrderAsync(database)).ShouldBe(["other"]);
    }

    private static async Task TheLineLetsOneWriterGoAtATimeAsync(SqliteTestDatabase database)
    {
        var hold = new HoldInTheTransaction();
        var first = SaveTenantAsync(database, "first", hold);
        await hold.Reached.Task.WaitAsync(Limit);
        var second = SaveTenantAsync(database, "second");
        await WaitForWritersAsync(database, 2);
        hold.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Limit);
        SqliteWriterLine.WritersInLineWithTurnHolder(database.ConnectionString).ShouldBe(0);
    }

    private static async Task WaitForWritersAsync(SqliteTestDatabase database, int count)
    {
        using var limit = new CancellationTokenSource(Limit);
        while (SqliteWriterLine.WritersInLineWithTurnHolder(database.ConnectionString) != count)
            await Task.Delay(TimeSpan.FromMilliseconds(5), limit.Token);
    }

    // A wait for the SQLite lock blocks its thread, so each writer needs a thread that is not the thread of the test.
    private static Task SaveTenantAsync(SqliteTestDatabase database, string name, HoldInTheTransaction? hold = null,
        CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await using var db = CreateContext(database, hold);
            db.Tenants.Add(Tenant.Create(new TenantForCreation { Name = name }));
            await db.SaveChangesAsync(cancellationToken);
        }, CancellationToken.None);

    private static async Task SeedLoginAttemptAsync(SqliteTestDatabase database)
    {
        await using var db = CreateContext(database);
        db.Set<LoginAttempt>().Add(new LoginAttempt
        {
            StateHash = Guid.NewGuid().ToString("N"),
            BindingHash = "binding",
            ModeId = "stub",
            Nonce = "nonce",
            PkceVerifier = "verifier",
            RedirectUri = "https://example.test/callback",
            ClientKind = SessionKind.Web,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> TenantNamesInCommitOrderAsync(SqliteTestDatabase database)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT name FROM tenants ORDER BY rowid", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<SqliteTestDatabase> MigratedDatabaseAsync()
    {
        var database = new SqliteTestDatabase();
        await using var db = CreateContext(database);
        await db.Database.MigrateAsync();
        return database;
    }

    private static AppDbContext CreateContext(SqliteTestDatabase database, HoldInTheTransaction? hold = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(SqliteConnectionSetup.Instance);
        if (hold is not null)
            builder.AddInterceptors(hold);
        return new AppDbContext(builder.Options, TimeProvider.System, Substitute.For<ICurrentUserService>(),
            Substitute.For<ICallerIdentityResolver>(), Substitute.For<ITenantScopeProvider>(),
            new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()));
    }

    private sealed class HoldInTheTransaction : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(Limit, cancellationToken);
            return result;
        }
    }
}
