namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Shouldly;

/// <summary>
/// The test uses a model of its own on a temporary table, so the production model has no fake entity. A temporary
/// table lives only in the session of its connection, so other tests do not see it, and it goes away when the test
/// closes the connection.
/// </summary>
public class StaleVersionIndexTests : TestBase
{
    private const string CurrentRevisionIndex = "ix_revisions_document_id_current";

    [Test]
    public async Task second_current_revision_of_a_document_is_a_stale_version()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();
        await using (var command = new SqliteCommand(
            $"""
            CREATE TEMP TABLE revisions (id uuid PRIMARY KEY, document_id uuid NOT NULL, is_current boolean NOT NULL);
            CREATE UNIQUE INDEX {CurrentRevisionIndex} ON revisions (document_id) WHERE is_current;
            """, connection))
            await command.ExecuteNonQueryAsync();

        await using var dbContext = new RevisionContext(connection);
        var documentId = Guid.NewGuid();
        dbContext.Add(new Revision { DocumentId = documentId, IsCurrent = true });
        await dbContext.SaveChangesAsync();
        dbContext.Add(new Revision { DocumentId = documentId, IsCurrent = true });

        // Act
        var failure = await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());

        // Assert
        failure.InnerException.ShouldBeOfType<SqliteException>().SqliteExtendedErrorCode.ShouldBe(2067);
        var exception = DatabaseConflict.From(failure, dbContext.Model).ShouldBeOfType<StaleVersionException>();
        exception.InnerException.ShouldBeSameAs(failure);
        DomainProblemDetails.StatusCodeFor(exception).ShouldBe(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task save_that_replaces_the_current_revision_is_not_stopped()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();
        await using (var command = new SqliteCommand(
            $"""
            CREATE TEMP TABLE revisions (id uuid PRIMARY KEY, document_id uuid NOT NULL, is_current boolean NOT NULL);
            CREATE UNIQUE INDEX {CurrentRevisionIndex} ON revisions (document_id) WHERE is_current;
            """, connection))
            await command.ExecuteNonQueryAsync();

        await using var dbContext = new RevisionContext(connection);
        var documentId = Guid.NewGuid();
        var first = new Revision { DocumentId = documentId, IsCurrent = true };
        dbContext.Add(first);
        await dbContext.SaveChangesAsync();

        // Act & Assert - the save does not throw
        dbContext.Add(new Revision { DocumentId = documentId, IsCurrent = true });
        first.IsCurrent = false;
        await dbContext.SaveChangesAsync();
    }

    private sealed class RevisionContext(SqliteConnection connection) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite(connection);

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.MapConstraintErrors();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var revision = modelBuilder.Entity<Revision>();
            revision.ToTable("revisions");
            revision.Property(r => r.Id).HasColumnName("id");
            revision.Property(r => r.DocumentId).HasColumnName("document_id");
            revision.Property(r => r.IsCurrent).HasColumnName("is_current");
            revision.HasIndex(r => r.DocumentId)
                .IsUnique()
                .HasFilter("is_current")
                .HasDatabaseName(CurrentRevisionIndex)
                .ForStaleVersion();
        }
    }

    private sealed class Revision
    {
        public Guid Id { get; set; }
        public Guid DocumentId { get; set; }
        public bool IsCurrent { get; set; }
    }
}
