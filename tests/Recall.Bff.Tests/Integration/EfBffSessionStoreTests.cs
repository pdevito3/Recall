namespace Recall.Bff.Tests.Integration;

using System.Data.Common;
using Recall.Bff.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Shouldly;

/// <summary>
/// The SQL of the EF Core adapter on the <c>bffdb</c> of the test run.
/// </summary>
public class EfBffSessionStoreTests
{
    [Test]
    [NotInParallel(BffSessionStoreContractTests.EndedRowDeletes)]
    public async Task each_cleanup_delete_takes_its_batch_from_the_start_of_an_expiry_index()
    {
        // Arrange
        var capture = new NonQueryCapture();
        var options = new DbContextOptionsBuilder<BffDbContext>();
        BffSessionExtensions.ConfigureBffDb(options, BffApp.Database.ConnectionString);
        options.AddInterceptors(capture);
        await using var dbContext = new BffDbContext(options.Options);
        var store = new EfBffSessionStore(dbContext, TimeProvider.System);

        // Act
        // A time in the past, so that the deletes change no row of another test
        await store.DeleteEndedAsync(new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero), 10, CancellationToken.None);

        // Assert
        capture.Commands.ShouldNotBeEmpty();
        await using var connection = new SqliteConnection(BffApp.Database.ConnectionString);
        await connection.OpenAsync();

        foreach (var command in capture.Commands)
        {
            var plan = await ExplainAsync(connection, command);
            plan.ShouldNotContain("TEMP B-TREE", customMessage: plan);
            plan.ShouldMatch(@"SEARCH \S+ USING (COVERING )?INDEX ix_sessions_(absolute_)?expires \((absolute_)?expires<", customMessage: plan);
        }
    }

    private static async Task<string> ExplainAsync(SqliteConnection connection, DbCommand command)
    {
        await using var explain = new SqliteCommand($"EXPLAIN QUERY PLAN {command.CommandText}", connection);
        foreach (SqliteParameter parameter in command.Parameters)
            explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);

        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(3));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Keeps a copy of each non-query command, such as the <c>DELETE</c> of <c>ExecuteDeleteAsync</c>.
    /// </summary>
    private sealed class NonQueryCapture : DbCommandInterceptor
    {
        public List<DbCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(Copy(command));
            return ValueTask.FromResult(result);
        }

        private static SqliteCommand Copy(DbCommand command)
        {
            var copy = new SqliteCommand(command.CommandText);
            foreach (SqliteParameter parameter in command.Parameters)
                copy.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
            return copy;
        }
    }
}
