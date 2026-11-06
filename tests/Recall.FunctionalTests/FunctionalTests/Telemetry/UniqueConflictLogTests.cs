namespace Recall.FunctionalTests.FunctionalTests.Telemetry;

using System.Diagnostics;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.Fakes.Tenant;
using Serilog.Events;
using Shouldly;

public class UniqueConflictLogTests : TestBase
{
    [Test]
    public async Task a_save_that_a_unique_index_stops_logs_one_information_line_with_no_stack_and_no_error()
    {
        // Arrange
        var name = $"Tenant {Guid.NewGuid():N}";
        await AddAsync(NewTenantNamed(name));
        using var trace = new Activity("unique-conflict-test").Start();

        // Act
        await Should.ThrowAsync<ConstraintConflictException>(() => ExecuteDbContextAsync(async db =>
        {
            db.Tenants.Add(NewTenantNamed(name));
            await db.SaveChangesAsync();
        }));

        // Assert
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(trace.TraceId);
        logs.ShouldNotContain(line => line.Level >= LogEventLevel.Warning);
        logs.ShouldAllBe(line => line.Exception == null);
        var line = logs.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogEventLevel.Information);
        line.Properties["Constraint"].ToString().ShouldContain("ix_tenants_name");
        line.Properties.Values.ShouldNotContain(value => value.ToString().Contains(name, StringComparison.Ordinal));
    }

    private static Tenant NewTenantNamed(string name)
        => Tenant.Create(new FakeTenantForCreation().RuleFor(t => t.Name, _ => name).Generate());
}
