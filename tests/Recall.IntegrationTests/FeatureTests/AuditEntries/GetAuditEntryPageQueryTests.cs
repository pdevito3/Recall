namespace Recall.IntegrationTests.FeatureTests.AuditEntries;

using Recall.SharedTestHelpers.Fakes.User;
using Recall.Server.Domain.AuditEntries.Dtos;
using Recall.Server.Domain.AuditEntries.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class GetAuditEntryPageQueryTests : TestBase
{
    [Test]
    public async Task pages_of_a_history_follow_each_other_newest_first_with_no_gap_and_no_repeat()
    {
        // Arrange
        var (scope, _, target) = await ArrangeReaderAsync(Permission.ReadAuditLog(), Permission.ReadAuditLogSensitive());
        var createdName = target.FirstName;
        await RenameAsync(scope, target.Id, "First", "Second", "Third");

        // Act
        var first = await scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From(target.Id, null, 3)));
        var second = await scope.SendAsync(new GetAuditEntryPage.Query(
            AuditEntryParametersDto.From(target.Id, first.NextCursor, 3)));

        // Assert
        first.NextCursor.ShouldNotBeNull();
        second.NextCursor.ShouldBeNull();
        first.Items.Concat(second.Items)
            .Select(e => (e.Kind, e.Values.SingleOrDefault(v => v.Name == "FirstName")?.New))
            .ShouldBe([("Updated", "Third"), ("Updated", "Second"), ("Updated", "First"), ("Created", createdName)]);
    }

    [Test]
    public async Task paging_a_history_of_one_size_at_a_time_shows_each_entry_once_in_the_order_of_the_instants()
    {
        // Arrange
        var (scope, _, target) = await ArrangeReaderAsync(Permission.ReadAuditLog());
        await RenameAsync(scope, target.Id, "First", "Second", "Third", "Fourth", "Fifth");
        var expected = await scope.ExecuteDbContextAsync(db => db.AuditEntries.AsNoTracking()
            .Where(e => e.RootId == target.Id)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.RecordId).ThenByDescending(e => e.Ordinal)
            .Select(e => e.OccurredAt)
            .ToListAsync());

        // Act
        var seen = new List<DateTimeOffset>();
        string? cursor = null;
        do
        {
            var page = await scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From(target.Id, cursor, 1)));
            seen.AddRange(page.Items.Select(e => e.OccurredAt));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Assert
        expected.Count.ShouldBe(6);
        seen.ShouldBe(expected);
        seen.ShouldBeInOrder(SortDirection.Descending);
    }

    [Test]
    public async Task reader_without_the_sensitive_permission_sees_hidden_personal_values_and_visible_others()
    {
        // Arrange
        var (scope, _, target) = await ArrangeReaderAsync(Permission.ReadAuditLog());

        // Act
        var page = await scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From(target.Id, null, null)));

        // Assert
        var values = page.Items.ShouldHaveSingleItem().Values.ToDictionary(v => v.Name);
        values["FirstName"].ShouldBe(new AuditValueDto { Name = "FirstName", Hidden = true });
        values["Email"].Hidden.ShouldBeTrue();
        values["TenantId"].ShouldBe(new AuditValueDto { Name = "TenantId", New = target.TenantId });
        values["Identifier"].ShouldBe(new AuditValueDto { Name = "Identifier", Hidden = true });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task actor_shows_by_its_ids_and_its_subject_only_to_a_reader_of_sensitive_values(bool canReadSensitive)
    {
        // Arrange
        Permission[] permissions = canReadSensitive
            ? [Permission.ReadAuditLog(), Permission.ReadAuditLogSensitive()]
            : [Permission.ReadAuditLog()];
        var (scope, reader, target) = await ArrangeReaderAsync(permissions);
        await RenameAsync(scope, target.Id, "Renamed");

        // Act
        var page = await scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From(target.Id, null, null)));

        // Assert
        var updated = page.Items.Single(e => e.Kind == "Updated");
        (updated.ActorSubject, updated.ActorUserId, updated.ActorAgentId, updated.ActorClient)
            .ShouldBe((canReadSensitive ? reader.Identifier : null, reader.Id, null, TestFixture.FirstPartyClientId));
    }

    [Test]
    public async Task reader_without_read_audit_log_is_forbidden_for_any_id()
    {
        // Arrange
        var (scope, _, _) = await ArrangeReaderAsync(Permission.ReadAuditLogSensitive());

        // Act
        var act = () => scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From("usr_unknown", null, null)));

        // Assert
        (await act.ShouldThrowAsync<ForbiddenAccessException>()).Message
            .ShouldBe("You cannot read the audit log. Ask an admin for access.");
    }

    [Test]
    public async Task tenant_page_has_only_the_entries_of_the_tenant_of_the_reader()
    {
        // Arrange
        var (scope, _, target) = await ArrangeReaderAsync(Permission.ReadAuditLog());
        var otherScope = new TestingServiceScope();
        await otherScope.StartGraph(out _).AddTenant(out _).AddUser(out var stranger).BuildAsync();

        // Act
        var tenantPage = await scope.SendAsync(new GetAuditEntryPage.Query(
            AuditEntryParametersDto.From(null, null, 500)));
        var strangerPage = await scope.SendAsync(new GetAuditEntryPage.Query(
            AuditEntryParametersDto.From(stranger.Id, null, null)));

        // Assert
        tenantPage.Items.ShouldContain(e => e.EntityId == target.Id);
        tenantPage.Items.ShouldAllBe(e => e.TenantId == target.TenantId);
        strangerPage.Items.ShouldBeEmpty();
    }

    [Test]
    public async Task cursor_that_the_server_did_not_make_is_a_query_input_error()
    {
        // Arrange
        var (scope, _, target) = await ArrangeReaderAsync(Permission.ReadAuditLog());

        // Act
        var act = () => scope.SendAsync(new GetAuditEntryPage.Query(AuditEntryParametersDto.From(target.Id, "page-2", null)));

        // Assert
        (await act.ShouldThrowAsync<QueryInputException>()).Parameter.ShouldBe("cursor");
    }

    private static async Task<(TestingServiceScope Scope, User Reader, User Target)> ArrangeReaderAsync(
        params Permission[] permissions)
    {
        var scope = new TestingServiceScope();
        var graph = scope.StartGraph(out _)
            .AddTenant(out _)
            .AddUsers(out var users, u => u.Roles = [], u => u.Roles = []);
        users[0].AddPermissions(permissions);
        await graph.BuildAsync();

        scope.SetUserWithDefaults(identifier: users[0].Identifier);
        return (scope, users[0], users[1]);
    }

    private static async Task RenameAsync(TestingServiceScope scope, string userId, params string[] firstNames)
    {
        foreach (var firstName in firstNames)
        {
            await scope.ExecuteDbContextAsync(async db =>
            {
                var user = await db.Users.SingleAsync(u => u.Id == userId);
                user.UpdateFromIdp(firstName, user.LastName, user.Email?.Value, user.Username);
                return await db.SaveChangesAsync();
            });
        }
    }
}
