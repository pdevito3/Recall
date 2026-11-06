namespace Recall.IntegrationTests.Databases;

using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Resources;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class DateStorageTests : TestBase
{
    private static readonly DateTimeOffset Earliest = new(2031, 1, 1, 10, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Middle = new(2031, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MiddlePlusOneMicrosecond = Middle.AddTicks(10);
    private static readonly DateTimeOffset Latest = new(2031, 1, 1, 5, 0, 0, TimeSpan.FromHours(-5));

    [Test]
    public async Task an_ef_sort_gives_the_order_of_the_instants_for_any_offset()
    {
        // Arrange
        var ids = await AddAttemptsAsync();

        // Act
        var sorted = await ExecuteDbContextAsync(db => db.Set<LoginAttempt>()
            .Where(a => ids.Contains(a.StateHash)).OrderBy(a => a.ExpiresAt).Select(a => a.StateHash).ToListAsync());

        // Assert
        sorted.ShouldBe(ids);
    }

    [Test]
    public async Task an_ef_filter_with_an_offset_compares_instants()
    {
        // Arrange
        var ids = await AddAttemptsAsync();
        var sameInstantWithAnOffset = Middle.ToOffset(TimeSpan.FromHours(-7));

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<LoginAttempt>()
            .Where(a => ids.Contains(a.StateHash) && a.ExpiresAt >= sameInstantWithAnOffset).OrderBy(a => a.ExpiresAt).Select(a => a.StateHash).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_querykit_date_filter_compares_instants()
    {
        // Arrange
        var ids = await AddAttemptsAsync();

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<LoginAttempt>()
            .Where(a => ids.Contains(a.StateHash))
            .ApplyFilters("""ExpiresAt >= "2031-01-01T02:00:00-07:00" """, new CustomQueryKitConfiguration())
            .OrderBy(a => a.ExpiresAt).Select(a => a.StateHash).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_from_sql_parameter_compares_instants()
    {
        // Arrange
        var ids = await AddAttemptsAsync();
        var instant = Middle.ToOffset(TimeSpan.FromHours(3));

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<LoginAttempt>()
            .FromSql($"SELECT * FROM auth_login_attempts WHERE expires_at >= {instant}")
            .Where(a => ids.Contains(a.StateHash)).OrderBy(a => a.ExpiresAt).Select(a => a.StateHash).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_sql_query_parameter_compares_instants()
    {
        // Arrange
        var ids = await AddAttemptsAsync();
        var instant = Middle.ToOffset(TimeSpan.FromHours(3));

        // Act
        var count = await ExecuteDbContextAsync(db => db.Database.SqlQuery<int>(
            $"SELECT count(*) AS \"Value\" FROM auth_login_attempts WHERE expires_at >= {instant} AND state_hash LIKE {ids[0][..^1] + "%"}").SingleAsync());

        // Assert
        count.ShouldBe(3);
    }

    [Test]
    public async Task an_execute_sql_parameter_stores_the_same_text_as_ef_and_a_null_stores_null()
    {
        // Arrange
        var id = Guid.NewGuid().ToString("N");
        var withAnOffset = Middle.ToOffset(TimeSpan.FromHours(3));

        // Act
        await ExecuteDbContextAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO auth_login_attempts (state_hash, binding_hash, mode_id, nonce, pkce_verifier, redirect_uri, client_kind, expires_at) VALUES ({id}, 'binding', 'mode', 'nonce', 'verifier', 'https://recall.test/callback', 'web', {withAnOffset})"));
        DateTimeOffset? nothing = null;
        var nullIsNull = await ExecuteDbContextAsync(db => db.Database.SqlQuery<int>(
            $"SELECT {nothing} IS NULL AS \"Value\"").SingleAsync());

        // Assert
        var stored = await ExecuteDbContextAsync(db => db.Set<LoginAttempt>().SingleAsync(a => a.StateHash == id));
        stored.ExpiresAt.ShouldBe(Middle);
        var text = await ExecuteDbContextAsync(db => db.Database.SqlQuery<string>(
            $"SELECT expires_at AS \"Value\" FROM auth_login_attempts WHERE state_hash = {id}").SingleAsync());
        text.ShouldBe("2031-01-01T09:00:00.000000Z");
        nullIsNull.ShouldBe(1);
    }

    [Test]
    public async Task a_sql_query_reads_a_date_column_as_a_date()
    {
        // Arrange
        var ids = await AddAttemptsAsync();

        // Act
        var read = await ExecuteDbContextAsync(db => db.Database.SqlQuery<DateTimeOffset>(
            $"SELECT expires_at AS \"Value\" FROM auth_login_attempts WHERE state_hash = {ids[1]}").SingleAsync());

        // Assert
        read.ShouldBe(Middle);
    }

    private static Task<T> ExecuteDbContextAsync<T>(Func<AppDbContext, Task<T>> action)
        => new TestingServiceScope().ExecuteDbContextAsync(action);

    private static async Task<string[]> AddAttemptsAsync()
    {
        var run = Guid.NewGuid().ToString("N");
        var attempts = new[] { Earliest, Middle, MiddlePlusOneMicrosecond, Latest }
            .Select((expiresAt, i) => new LoginAttempt
            {
                StateHash = $"{run}{i}",
                BindingHash = "binding",
                ModeId = "mode",
                Nonce = "nonce",
                PkceVerifier = "verifier",
                RedirectUri = "https://recall.test/callback",
                ClientKind = SessionKind.Web,
                ExpiresAt = expiresAt,
            }).ToArray();
        await ExecuteDbContextAsync(async db =>
        {
            db.AddRange(attempts.Reverse());
            return await db.SaveChangesAsync();
        });
        return attempts.Select(a => a.StateHash).ToArray();
    }
}
