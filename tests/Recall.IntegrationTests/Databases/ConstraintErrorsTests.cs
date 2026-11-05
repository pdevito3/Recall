namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Data.Sqlite;
using Shouldly;

public class ConstraintErrorsTests : TestBase
{
    // SQLite makes the index of a primary key by itself, and that index has no SQL.
    private const string ConstraintsSql =
        """
        SELECT m.name, group_concat(m.tbl_name || '.' || c.name, ', ')
        FROM sqlite_master m, pragma_index_info(m.name) c
        WHERE m.type = 'index'
          AND m.sql LIKE 'CREATE UNIQUE INDEX%'
          AND m.tbl_name <> '__EFMigrationsHistory'
        GROUP BY m.name
        """;

    [Test]
    public async Task each_unique_index_in_the_schema_declares_a_request_field_or_a_stale_version()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();

        // Act
        var constraints = await ConstraintsAsync(connection, "sqlite_master");

        // Assert
        constraints.ShouldNotBeEmpty();
        Undeclared(constraints, AppModel()).ShouldBeEmpty();
    }

    [Test]
    public async Task unique_index_that_declares_neither_is_undeclared()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();

        // A temp table lives on this connection, so other tests never see it.
        await using (var command = new SqliteCommand(
            """
            CREATE TEMP TABLE constraint_guard_probes (id TEXT PRIMARY KEY, code TEXT NOT NULL);
            CREATE UNIQUE INDEX ix_constraint_guard_probes_code ON constraint_guard_probes (code);
            """, connection))
            await command.ExecuteNonQueryAsync();

        // Act
        var constraints = await ConstraintsAsync(connection, "sqlite_temp_master");

        // Assert
        Undeclared(constraints, AppModel()).ShouldBe(["ix_constraint_guard_probes_code"]);
    }

    private static IModel AppModel() => new TestingServiceScope().GetService<AppDbContext>().Model;

    private static IEnumerable<string> Undeclared(IEnumerable<(string Name, string Columns)> constraints, IModel model)
    {
        var errors = ConstraintErrors.Of(model);
        return constraints
            .Where(constraint => !errors.ContainsKey(constraint.Name) && !errors.ContainsKey(constraint.Columns))
            .Select(constraint => constraint.Name);
    }

    private static async Task<List<(string Name, string Columns)>> ConstraintsAsync(SqliteConnection connection, string schemaTable)
    {
        await using var command = new SqliteCommand(ConstraintsSql.Replace("sqlite_master", schemaTable, StringComparison.Ordinal), connection);
        await using var reader = await command.ExecuteReaderAsync();
        var constraints = new List<(string Name, string Columns)>();
        while (await reader.ReadAsync())
            constraints.Add((reader.GetString(0), reader.GetString(1)));
        return constraints;
    }
}
