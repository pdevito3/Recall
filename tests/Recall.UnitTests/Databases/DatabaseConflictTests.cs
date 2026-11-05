namespace Recall.UnitTests.Databases;

using System.Diagnostics;
using Recall.Server.Databases;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

public class DatabaseConflictTests
{
    // ASP.NET Core sets the default type of the status before Customize runs
    private const string DefaultConflictType = "https://tools.ietf.org/html/rfc9110#section-15.5.10";

    private const string CurrentRevisionIndex = "ix_revisions_document_id_current";
    private const string DuplicateIdentifier = "UNIQUE constraint failed: users.identifier";
    private const string DuplicateCurrentRevision = "UNIQUE constraint failed: Revision.DocumentId";
    private const string DuplicateRevisionNumber = "UNIQUE constraint failed: Revision.Number";
    private const int UniqueViolation = 2067;
    private const int ForeignKeyViolation = 787;

    private static readonly IModel Model = new AppDbContextFactory().CreateDbContext([]).Model;
    private static readonly IModel RevisionModel = new RevisionContext().Model;

    [Test]
    public void unique_violation_is_a_conflict_with_the_field_and_the_message_of_the_index()
    {
        // Act
        var exception = DatabaseConflict.From(SaveFailure(UniqueViolation, DuplicateIdentifier), Model)
            .ShouldBeOfType<ConstraintConflictException>();

        // Assert
        exception.Constraint.ShouldBe("ix_users_identifier");
        exception.Property.ShouldBe("identifier");
        exception.Message.ShouldBe("A user with this identifier already exists.");
        DomainProblemDetails.StatusCodeFor(exception).ShouldBe(StatusCodes.Status409Conflict);
        DomainError.IsClientError(exception).ShouldBeTrue();
    }

    [Test]
    public void unique_violation_problem_has_the_message_under_the_field_of_the_index()
    {
        // Arrange
        var context = ContextFor(DatabaseConflict.From(SaveFailure(UniqueViolation, DuplicateIdentifier), Model)!);

        // Act
        DomainProblemDetails.Customize(context);

        // Assert
        context.ProblemDetails.Type.ShouldBe(DefaultConflictType);
        context.ProblemDetails.Title.ShouldBe("The request conflicts with a record that exists.");
        var error = context.ProblemDetails.Extensions["errors"].ShouldBeOfType<Dictionary<string, string[]>>().ShouldHaveSingleItem();
        error.Key.ShouldBe("identifier");
        error.Value.ShouldBe(["A user with this identifier already exists."]);
    }

    [Test]
    public void unique_violation_problem_does_not_show_the_constraint()
    {
        // Arrange
        var context = ContextFor(DatabaseConflict.From(SaveFailure(UniqueViolation, "UNIQUE constraint failed: other_table.other_column"), Model)!);

        // Act
        DomainProblemDetails.Customize(context);

        // Assert
        context.ProblemDetails.Detail.ShouldBe("A record with this value already exists.");
        context.ProblemDetails.Extensions["errors"].ShouldBeOfType<Dictionary<string, string[]>>().ShouldBeEmpty();
    }

    [Test]
    public void concurrency_failure_is_a_stale_version()
    {
        // Act
        var exception = DatabaseConflict.From(new DbUpdateConcurrencyException("The row changed."), Model)
            .ShouldBeOfType<StaleVersionException>();

        // Assert
        exception.Message.ShouldBe("This record changed after you loaded it. Reload it, then make your change again.");
        DomainProblemDetails.StatusCodeFor(exception).ShouldBe(StatusCodes.Status409Conflict);
        DomainError.IsClientError(exception).ShouldBeTrue();
    }

    [Test]
    public void concurrency_failure_problem_has_the_stale_version_type_and_the_message_under_the_version()
    {
        // Arrange
        var context = ContextFor(DatabaseConflict.From(new DbUpdateConcurrencyException("The row changed."), Model)!);

        // Act
        DomainProblemDetails.Customize(context);

        // Assert
        context.ProblemDetails.Type.ShouldBe(DomainProblemDetails.StaleVersionType);
        context.ProblemDetails.Title.ShouldBe("The record changed after you loaded it.");
        context.ProblemDetails.Extensions["errors"].ShouldBeOfType<Dictionary<string, string[]>>()
            .ShouldHaveSingleItem().Key.ShouldBe("version");
    }

    [Test]
    public void unique_violation_of_a_stale_version_index_is_a_stale_version()
    {
        // Arrange
        var failure = SaveFailure(UniqueViolation, DuplicateCurrentRevision);

        // Act
        var exception = DatabaseConflict.From(failure, RevisionModel).ShouldBeOfType<StaleVersionException>();

        // Assert
        exception.InnerException.ShouldBeSameAs(failure);
        DomainProblemDetails.StatusCodeFor(exception).ShouldBe(StatusCodes.Status409Conflict);
        DomainError.IsClientError(exception).ShouldBeTrue();
    }

    [Test]
    public void unique_violation_of_a_stale_version_index_has_the_problem_of_a_concurrency_failure()
    {
        // Arrange
        var staleIndex = DatabaseConflict.From(SaveFailure(UniqueViolation, DuplicateCurrentRevision), RevisionModel)!;
        var staleRow = DatabaseConflict.From(new DbUpdateConcurrencyException("The row changed."), Model)!;
        var (staleIndexContext, staleRowContext) = (ContextFor(staleIndex), ContextFor(staleRow));

        // Act
        DomainProblemDetails.Customize(staleIndexContext);
        DomainProblemDetails.Customize(staleRowContext);

        // Assert
        DomainProblemDetails.StatusCodeFor(staleIndex).ShouldBe(DomainProblemDetails.StatusCodeFor(staleRow));
        var (problem, expected) = (staleIndexContext.ProblemDetails, staleRowContext.ProblemDetails);
        problem.Type.ShouldBe(DomainProblemDetails.StaleVersionType);
        problem.Type.ShouldBe(expected.Type);
        problem.Title.ShouldBe(expected.Title);
        problem.Detail.ShouldBe(expected.Detail);
        problem.Extensions.Keys.ShouldBe(expected.Extensions.Keys);
        var errors = problem.Extensions["errors"].ShouldBeOfType<Dictionary<string, string[]>>();
        var expectedErrors = expected.Extensions["errors"].ShouldBeOfType<Dictionary<string, string[]>>();
        errors.Keys.ShouldBe(expectedErrors.Keys);
        errors[StaleVersionException.Field].ShouldBe(expectedErrors[StaleVersionException.Field]);
    }

    [Test]
    public void unique_violation_of_another_index_of_a_model_with_a_stale_version_index_is_a_conflict()
    {
        // Act
        var exception = DatabaseConflict.From(SaveFailure(UniqueViolation, DuplicateRevisionNumber), RevisionModel)
            .ShouldBeOfType<ConstraintConflictException>();

        // Assert
        exception.Property.ShouldBe("number");
        exception.Message.ShouldBe("A revision with this number already exists.");
    }

    [Test]
    public void unique_violation_of_an_index_with_an_unknown_constraint_error_throws()
    {
        // Arrange
        var failure = SaveFailure(UniqueViolation, DuplicateRevisionNumber);

        // Act
        var exception = Should.Throw<UnreachableException>(() => DatabaseConflict.From(failure, new UnknownErrorContext().Model));

        // Assert
        exception.Message.ShouldContain("ix_revisions_number");
        exception.Message.ShouldContain(typeof(UnknownError).FullName!);
    }

    [Test]
    public void an_index_that_only_a_migration_adds_is_known_by_the_columns_that_the_error_names()
    {
        // Act
        var name = DatabaseConflict.ConstraintNameOf(
            "SQLite Error 19: 'UNIQUE constraint failed: users.tenant_id, users.first_name'.", Model);

        // Assert
        name.ShouldBe("users.tenant_id, users.first_name");
    }

    [Test]
    public void other_database_failure_is_not_a_conflict()
    {
        // Act & Assert
        DatabaseConflict.From(SaveFailure(ForeignKeyViolation, "FOREIGN KEY constraint failed"), Model).ShouldBeNull();
    }

    private static ProblemDetailsContext ContextFor(Exception exception) => new()
    {
        HttpContext = new DefaultHttpContext(),
        Exception = exception,
        ProblemDetails = new() { Status = StatusCodes.Status409Conflict, Type = DefaultConflictType },
    };

    private sealed class RevisionContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite("Data Source=unused.db");

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.MapConstraintErrors();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var revision = modelBuilder.Entity<Revision>();
            revision.HasIndex(r => r.DocumentId)
                .IsUnique()
                .HasFilter("is_current")
                .HasDatabaseName(CurrentRevisionIndex)
                .ForStaleVersion();
            revision.HasIndex(r => r.Number)
                .IsUnique()
                .HasDatabaseName("ix_revisions_number")
                .ForRequestField("number", "A revision with this number already exists.");
        }
    }

    private sealed class UnknownErrorContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite("Data Source=unused.db");

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.MapConstraintErrors();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var index = modelBuilder.Entity<Revision>().HasIndex(r => r.Number)
                .IsUnique()
                .HasDatabaseName("ix_revisions_number")
                .ForRequestField("number", "A revision with this number already exists.");
            var annotation = index.Metadata.GetAnnotations().Single(a => a.Value is ConstraintError);
            index.HasAnnotation(annotation.Name, new UnknownError());
        }
    }

    // The protected copy constructor that the compiler adds to a record lets a third subtype derive
    private sealed record UnknownError : ConstraintError
    {
        public UnknownError()
            : base(new StaleVersion())
        {
        }
    }

    private sealed class Revision
    {
        public int Id { get; set; }
        public int DocumentId { get; set; }
        public int Number { get; set; }
        public bool IsCurrent { get; set; }
    }

    private static DbUpdateException SaveFailure(int extendedCode, string message)
        => new("An error occurred while saving the entity changes.",
            new SqliteException($"SQLite Error 19: '{message}'.", 19, extendedCode));
}
