namespace Recall.UnitTests.Resources.Entrypoints;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints;
using Shouldly;

public class DomainErrorDatabaseBusyTests
{
    private const int BusyErrorCode = 5;

    [Test]
    public void a_write_that_did_not_get_the_database_in_time_is_a_server_fault_that_says_to_try_again()
    {
        // Arrange
        var lockWait = new SqliteException("database is locked", BusyErrorCode);

        // Act
        var failures = new Exception[] { new DatabaseBusyException(), lockWait, new DbUpdateException("save", lockWait) };

        // Assert
        foreach (var failure in failures)
        {
            var error = DomainError.From(failure);
            error.Kind.ShouldBe(DomainErrorKind.DatabaseBusy);
            error.Detail.ShouldNotBeNull().ShouldContain("Try again");
            DomainProblemDetails.StatusCodeFor(failure).ShouldBe(StatusCodes.Status503ServiceUnavailable);
            DomainError.IsClientError(failure).ShouldBeFalse();
        }
    }
}
