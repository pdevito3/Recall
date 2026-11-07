namespace Recall.UnitTests.Resources.Entrypoints;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Exceptions;
using Shouldly;

public class DomainErrorDiskFullTests
{
    private const int FullErrorCode = 13;
    private const int ConstraintErrorCode = 19;

    [Test]
    public void a_full_disk_is_a_server_fault_that_says_so_with_or_without_the_wrapper_of_a_save()
    {
        // Arrange
        var raw = new SqliteException("database or disk is full", FullErrorCode);
        var wrapped = new DbUpdateException("save", raw);

        // Act
        var failures = new Exception[] { raw, wrapped };

        // Assert
        foreach (var failure in failures)
        {
            var error = DomainError.From(failure);
            error.Kind.ShouldBe(DomainErrorKind.DiskFull);
            error.Detail.ShouldNotBeNull().ShouldContain("free some space");
            DomainError.IsClientError(failure).ShouldBeFalse();
        }
    }

    [Test]
    public void another_database_error_stays_a_generic_fault()
    {
        // Act
        var error = DomainError.From(new DbUpdateException("save", new SqliteException("constraint", ConstraintErrorCode)));

        // Assert
        error.Kind.ShouldNotBe(DomainErrorKind.DiskFull);
    }
}
