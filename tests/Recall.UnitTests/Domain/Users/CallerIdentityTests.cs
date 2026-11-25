namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Shouldly;

public sealed class CallerIdentityTests
{
    private static readonly string TenantId = EntityId.New(Tenant.IdPrefix);
    private static readonly string AgentId = EntityId.New(Agent.IdPrefix);
    private static readonly LiveUser Person = new("usr_1", TenantId);

    private static CallerIdentity.PersonalAccessToken TokenWith(JournalAccess journal)
        => new("pat_1", "user-1", Person, new TokenGrant([Permission.ManageUsers()], journal));

    public static IEnumerable<Func<(string Case, CallerIdentity Identity, bool CanManage)>> ManageAllTenantsCases()
    {
        yield return () => ("system work", new CallerIdentity.System(), true);
        yield return () => ("client on the manage list", new CallerIdentity.AllTenantsClient("sync", CanManage: true), true);
        yield return () => ("client on the read list", new CallerIdentity.AllTenantsClient("report", CanManage: false), false);
        yield return () => ("user", new CallerIdentity.User("user-1", new LiveUser("usr_1", TenantId)), false);
        yield return () => ("delegated agent",
            new CallerIdentity.DelegatedAgent(AgentId, "clinic-assistant", "user-1", new LiveUser("usr_1", TenantId)), false);
        yield return () => ("machine agent", new CallerIdentity.MachineAgent(AgentId, "fleet-sync", TenantId), false);
        yield return () => ("personal access token", TokenWith(JournalAccess.ReadWrite), false);
        yield return () => ("unknown client", new CallerIdentity.UnknownClient("m2m"), false);
        yield return () => ("anonymous", new CallerIdentity.Anonymous(), false);
    }

    [Test]
    [MethodDataSource(nameof(ManageAllTenantsCases))]
    public void only_system_work_and_a_client_on_the_manage_list_can_manage_all_tenants(
        string @case, CallerIdentity identity, bool canManage)
    {
        // Act & Assert
        identity.CanManageAllTenants.ShouldBe(canManage);
    }

    public static IEnumerable<Func<(string Case, CallerIdentity Identity, string? JournalOwnerId, bool CanWriteJournal, JournalAccess Journal)>> JournalCases()
    {
        yield return () => ("system work", new CallerIdentity.System(), null, false, JournalAccess.None);
        yield return () => ("client on the manage list", new CallerIdentity.AllTenantsClient("sync", CanManage: true), null, false, JournalAccess.None);
        yield return () => ("user", new CallerIdentity.User("user-1", Person), Person.UserId, true, JournalAccess.ReadWrite);
        yield return () => ("user with no user record", new CallerIdentity.User("user-1", null), null, false, JournalAccess.None);
        yield return () => ("journal work", new CallerIdentity.JournalWork(Person.UserId, TenantId), Person.UserId, true, JournalAccess.ReadWrite);
        yield return () => ("delegated agent",
            new CallerIdentity.DelegatedAgent(AgentId, "clinic-assistant", "user-1", Person), null, false, JournalAccess.None);
        yield return () => ("machine agent", new CallerIdentity.MachineAgent(AgentId, "fleet-sync", TenantId), null, false, JournalAccess.None);
        yield return () => ("token with no journal access", TokenWith(JournalAccess.None), null, false, JournalAccess.None);
        yield return () => ("token that reads the journal", TokenWith(JournalAccess.Read), Person.UserId, false, JournalAccess.Read);
        yield return () => ("token that reads and changes the journal", TokenWith(JournalAccess.ReadWrite), Person.UserId, true, JournalAccess.ReadWrite);
        yield return () => ("unknown client", new CallerIdentity.UnknownClient("m2m"), null, false, JournalAccess.None);
        yield return () => ("anonymous", new CallerIdentity.Anonymous(), null, false, JournalAccess.None);
    }

    [Test]
    [Property("Rule", "PAT-6")]
    [Property("Rule", "IMP-22")]
    [MethodDataSource(nameof(JournalCases))]
    public void a_caller_owns_a_journal_only_as_its_person_and_a_token_only_with_journal_access(
        string @case, CallerIdentity identity, string? journalOwnerId, bool canWriteJournal, JournalAccess journal)
    {
        // Act & Assert
        identity.JournalOwnerId.ShouldBe(journalOwnerId);
        identity.CanWriteJournal.ShouldBe(canWriteJournal);
        identity.Journal.ShouldBe(journal);
    }

    [Test]
    [Arguments("none")]
    [Arguments("read")]
    [Arguments("read_write")]
    public void a_token_sees_the_tenant_of_its_person_whatever_its_journal_access(string journal)
    {
        // Act
        var scope = TokenWith(JournalAccess.FromName(journal)).Scope;

        // Assert
        scope.ShouldBe(TenantScope.ForTenant(TenantId));
    }
}
