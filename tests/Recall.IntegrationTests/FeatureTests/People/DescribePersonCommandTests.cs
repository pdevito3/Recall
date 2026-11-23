namespace Recall.IntegrationTests.FeatureTests.People;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.People.Features;
using Shouldly;

public class DescribePersonCommandTests : TestBase
{
    [Test]
    [Property("Rule", "SES-55")]
    public async Task a_detail_edit_writes_no_audit_entry_and_no_entry_holds_a_detail()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var jane = await scope.SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Jane", FullName = "Jane Park" }));

        // Act
        await scope.SendAsync(new DescribePerson.Command(jane.Id, new PersonDetailsDto { FullName = "Jane Doe", Notes = "Sam's teacher", Version = jane.Version }));

        // Assert
        var entries = await scope.ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters().Where(e => e.EntityId == jane.Id).ToListAsync());
        entries.Select(e => e.Kind).ShouldBe([AuditEntryKind.Created]);
        entries.ShouldAllBe(e => !e.Data.Contains("Jane"));
        entries.ShouldAllBe(e => !e.Data.Contains("Sam's teacher"));
    }
}
