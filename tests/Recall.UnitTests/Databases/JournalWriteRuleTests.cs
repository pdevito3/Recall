namespace Recall.UnitTests.Databases;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Databases;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;

public class JournalWriteRuleTests
{
    private static readonly LiveUser Person = new("usr_1", "ten_1");

    private static CallerIdentity.PersonalAccessToken TokenWith(JournalAccess journal)
        => new("pat_1", "user-1", Person, new TokenGrant(Permission.GetAll(), journal));

    [Test]
    [Property("Rule", "PAT-7")]
    public void the_journal_types_are_the_types_with_the_owner_filter()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);

        // Act
        var journal = context.Model.GetEntityTypes().Where(JournalWriteRule.IsJournal).Select(e => e.ClrType.Name);

        // Assert
        journal.ShouldBe(
        [
            "CleanedRevision", "Correction", "Person", "RawRevision", "Recording", "RecordingSlice", "Session",
            "Summary", "SyncChange", "UserSetting",
        ], ignoreOrder: true);
    }

    [Test]
    [Property("Rule", "PAT-7")]
    [Arguments(EntityState.Added)]
    [Arguments(EntityState.Modified)]
    [Arguments(EntityState.Deleted)]
    public void a_token_that_only_reads_the_journal_cannot_change_a_journal_row(EntityState state)
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);
        TestGraph.Start(out var seed).Under(seed.User).AddPerson(out var person);
        context.Entry(person).State = state;

        // Act
        var check = () => JournalWriteRule.Check(TokenWith(JournalAccess.Read), context.ChangeTracker.Entries());

        // Assert
        check.ShouldThrow<ForbiddenAccessException>().Message
            .ShouldBe("This personal access token can read your journal but cannot change it.");
    }

    public static IEnumerable<Func<(string Case, CallerIdentity Caller)>> CallersThatPass()
    {
        yield return () => ("token that reads and changes the journal", TokenWith(JournalAccess.ReadWrite));
        yield return () => ("user", new CallerIdentity.User("user-1", Person));
        yield return () => ("user with no user record", new CallerIdentity.User("user-1", null));
        yield return () => ("journal work", new CallerIdentity.JournalWork(Person.UserId, Person.TenantId));
        yield return () => ("system work", new CallerIdentity.System());
        yield return () => ("delegated agent", new CallerIdentity.DelegatedAgent("agt_1", "clinic-assistant", "user-1", Person));
        yield return () => ("machine agent", new CallerIdentity.MachineAgent("agt_1", "fleet-sync", Person.TenantId));
        yield return () => ("unknown client", new CallerIdentity.UnknownClient("m2m"));
        yield return () => ("anonymous", new CallerIdentity.Anonymous());
    }

    [Test]
    [Property("Rule", "PAT-7")]
    [MethodDataSource(nameof(CallersThatPass))]
    public void the_rule_refuses_no_other_caller(string @case, CallerIdentity caller)
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);
        TestGraph.Start(out var seed).Under(seed.User).AddPerson(out var person);
        context.Entry(person).State = EntityState.Modified;

        // Act
        var check = () => JournalWriteRule.Check(caller, context.ChangeTracker.Entries());

        // Assert
        check.ShouldNotThrow();
    }

    [Test]
    [Property("Rule", "PAT-7")]
    public void a_token_with_no_journal_write_changes_other_rows_and_reads_journal_rows()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);
        TestGraph.Start(out var seed).Under(seed.User).AddPerson(out var person);
        context.Entry(seed.User).State = EntityState.Modified;
        context.Entry(person).State = EntityState.Unchanged;

        // Act
        var readOnly = () => JournalWriteRule.Check(TokenWith(JournalAccess.Read), context.ChangeTracker.Entries());
        var none = () => JournalWriteRule.Check(TokenWith(JournalAccess.None), context.ChangeTracker.Entries());

        // Assert
        readOnly.ShouldNotThrow();
        none.ShouldNotThrow();
    }
}
