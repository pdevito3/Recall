namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain.DPoPProofs;
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
        var ids = await AddProofsAsync();

        // Act
        var sorted = await ExecuteDbContextAsync(db => db.Set<UsedDPoPProof>()
            .Where(p => ids.Contains(p.Id)).OrderBy(p => p.Expires).Select(p => p.Id).ToListAsync());

        // Assert
        sorted.ShouldBe(ids);
    }

    [Test]
    public async Task an_ef_filter_with_an_offset_compares_instants()
    {
        // Arrange
        var ids = await AddProofsAsync();
        var sameInstantWithAnOffset = Middle.ToOffset(TimeSpan.FromHours(-7));

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<UsedDPoPProof>()
            .Where(p => ids.Contains(p.Id) && p.Expires >= sameInstantWithAnOffset).OrderBy(p => p.Expires).Select(p => p.Id).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_querykit_date_filter_compares_instants()
    {
        // Arrange
        var ids = await AddProofsAsync();

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<UsedDPoPProof>()
            .Where(p => ids.Contains(p.Id))
            .ApplyFilters("""Expires >= "2031-01-01T02:00:00-07:00" """, new CustomQueryKitConfiguration())
            .OrderBy(p => p.Expires).Select(p => p.Id).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_from_sql_parameter_compares_instants()
    {
        // Arrange
        var ids = await AddProofsAsync();
        var instant = Middle.ToOffset(TimeSpan.FromHours(3));

        // Act
        var found = await ExecuteDbContextAsync(db => db.Set<UsedDPoPProof>()
            .FromSql($"SELECT * FROM used_dpop_proofs WHERE expires >= {instant}")
            .Where(p => ids.Contains(p.Id)).OrderBy(p => p.Expires).Select(p => p.Id).ToListAsync());

        // Assert
        found.ShouldBe(ids[1..]);
    }

    [Test]
    public async Task a_sql_query_parameter_compares_instants()
    {
        // Arrange
        var ids = await AddProofsAsync();
        var instant = Middle.ToOffset(TimeSpan.FromHours(3));

        // Act
        var count = await ExecuteDbContextAsync(db => db.Database.SqlQuery<int>(
            $"SELECT count(*) AS \"Value\" FROM used_dpop_proofs WHERE expires >= {instant} AND id LIKE {ids[0][..^1] + "%"}").SingleAsync());

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
            $"INSERT INTO used_dpop_proofs (id, expires) VALUES ({id}, {withAnOffset})"));
        DateTimeOffset? nothing = null;
        var nullIsNull = await ExecuteDbContextAsync(db => db.Database.SqlQuery<int>(
            $"SELECT {nothing} IS NULL AS \"Value\"").SingleAsync());

        // Assert
        var stored = await ExecuteDbContextAsync(db => db.Set<UsedDPoPProof>().SingleAsync(p => p.Id == id));
        stored.Expires.ShouldBe(Middle);
        var text = await ExecuteDbContextAsync(db => db.Database.SqlQuery<string>(
            $"SELECT expires AS \"Value\" FROM used_dpop_proofs WHERE id = {id}").SingleAsync());
        text.ShouldBe("2031-01-01T09:00:00.000000Z");
        nullIsNull.ShouldBe(1);
    }

    [Test]
    public async Task a_sql_query_reads_a_date_column_as_a_date()
    {
        // Arrange
        var ids = await AddProofsAsync();

        // Act
        var read = await ExecuteDbContextAsync(db => db.Database.SqlQuery<DateTimeOffset>(
            $"SELECT expires AS \"Value\" FROM used_dpop_proofs WHERE id = {ids[1]}").SingleAsync());

        // Assert
        read.ShouldBe(Middle);
    }

    private static Task<T> ExecuteDbContextAsync<T>(Func<AppDbContext, Task<T>> action)
        => new TestingServiceScope().ExecuteDbContextAsync(action);

    private static async Task<string[]> AddProofsAsync()
    {
        var run = Guid.NewGuid().ToString("N");
        var proofs = new[] { Earliest, Middle, MiddlePlusOneMicrosecond, Latest }
            .Select((expires, i) => new UsedDPoPProof($"{run}{i}", expires)).ToArray();
        await ExecuteDbContextAsync(async db =>
        {
            db.AddRange(proofs.Reverse());
            return await db.SaveChangesAsync();
        });
        return proofs.Select(p => p.Id).ToArray();
    }
}
