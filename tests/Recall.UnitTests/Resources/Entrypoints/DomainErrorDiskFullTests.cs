namespace Recall.UnitTests.Resources.Entrypoints;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints;
using Shouldly;

public class DomainErrorDiskFullTests
{
    private const int FullErrorCode = 13;
    private const int ConstraintErrorCode = 19;
    private const int NoSpaceErrno = 28;
    private const int LinuxQuotaErrno = 122;
    private const int MacQuotaErrno = 69;
    private const int ReadOnlyErrno = 30;
    private const int InputOutputErrno = 5;

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
    [Arguments(NoSpaceErrno)]
    [Arguments(LinuxQuotaErrno)]
    [Arguments(MacQuotaErrno)]
    public void a_file_write_with_no_room_gives_the_same_answer_as_the_database(int errno)
    {
        // Arrange
        var raw = new IOException("No room", errno);
        var wrapped = new InvalidOperationException("write", raw);

        // Act
        var failures = new Exception[] { raw, wrapped };

        // Assert
        foreach (var failure in failures)
        {
            DomainError.From(failure).Kind.ShouldBe(DomainErrorKind.DiskFull);
            DomainProblemDetails.StatusCodeFor(failure).ShouldBe(StatusCodes.Status507InsufficientStorage);
            DomainError.IsClientError(failure).ShouldBeFalse();
        }
    }

    [Test]
    [Arguments(ReadOnlyErrno)]
    [Arguments(InputOutputErrno)]
    public void a_disk_fault_that_is_not_a_full_disk_stays_a_fault(int errno)
    {
        // Act
        var failure = new IOException("A disk fault", errno);

        // Assert
        DomainError.From(failure).Kind.ShouldBe(DomainErrorKind.Unexpected);
        DomainProblemDetails.StatusCodeFor(failure).ShouldBe(StatusCodes.Status500InternalServerError);
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
