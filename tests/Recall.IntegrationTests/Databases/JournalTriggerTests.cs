namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sync;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Domain.Users.Features;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;

public class JournalTriggerTests : TestBase
{
    [Test]
    [Property("Rule", "SES-7")]
    [Arguments("sessions", "last_transcript_revision")]
    [Arguments("raw_revisions", "claimed_saved_at")]
    [Arguments("cleaned_revisions", "claimed_saved_at")]
    public async Task the_columns_of_the_rule_on_the_save_time_are_gone(string table, string column)
    {
        // Arrange
        var read = () => TestFixture.ExecuteOutOfBandAsync($"SELECT {column} FROM {table}");

        // Act
        var refused = await Should.ThrowAsync<SqliteException>(read);

        // Assert
        refused.Message.ShouldContain("no such column");
    }

    [Test]
    [Property("Rule", "SES-11")]
    [Arguments("raw_revisions")]
    [Arguments("cleaned_revisions")]
    public async Task the_database_refuses_an_update_of_a_revision(string table)
    {
        // Arrange
        var (_, session) = await SavedSessionAsync("Words that stay");

        // Act
        var update = () => TestFixture.ExecuteOutOfBandAsync(
            $"UPDATE {table} SET text = 'changed' WHERE session_id = @id", ("@id", session.Id));

        // Assert
        (await Should.ThrowAsync<SqliteException>(update)).Message.ShouldContain("append-only");
    }

    [Test]
    [Property("Rule", "SES-11")]
    [Arguments("raw_revisions")]
    [Arguments("cleaned_revisions")]
    public async Task the_database_refuses_a_delete_of_a_revision_whose_session_stays(string table)
    {
        // Arrange
        var (_, session) = await SavedSessionAsync("Words that stay");

        // Act
        var delete = () => TestFixture.ExecuteOutOfBandAsync(
            $"DELETE FROM {table} WHERE session_id = @id", ("@id", session.Id));

        // Assert
        (await Should.ThrowAsync<SqliteException>(delete)).Message.ShouldContain("only with its Session");
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_delete_of_a_session_removes_its_revisions_and_leaves_a_tombstone()
    {
        // Arrange
        var (scope, session) = await SavedSessionAsync("Words that go");

        // Act
        await scope.SendAsync(new DeleteSession.Command(session.Id));

        // Assert
        var rows = await scope.ExecuteDbContextAsync(async db => new
        {
            Session = await db.Sessions.IgnoreQueryFilters().CountAsync(s => s.Id == session.Id),
            Revisions = await db.RawRevisions.IgnoreQueryFilters().CountAsync(r => r.SessionId == session.Id),
            Line = await db.SyncChanges.IgnoreQueryFilters().SingleAsync(c => c.EntityId == session.Id),
        });
        rows.Session.ShouldBe(0);
        rows.Revisions.ShouldBe(0);
        rows.Line.IsTombstone.ShouldBeTrue();
        rows.Line.ClientSessionId.ShouldBe(session.ClientSessionId);
    }

    [Test]
    [Property("Rule", "SES-13")]
    public async Task a_delete_of_the_owner_removes_the_journal_and_its_feed()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUser(out var owner)
            .AddSession(out _)
            .Under(owner)
            .AddPerson(out _)
            .Under(owner)
            .AddCorrection(out _)
            .BuildAsync();

        // Act
        await scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        var left = await scope.ExecuteDbContextAsync(async db => new
        {
            Sessions = await db.Sessions.IgnoreQueryFilters().CountAsync(s => s.UserId == owner.Id),
            People = await db.People.IgnoreQueryFilters().CountAsync(p => p.UserId == owner.Id),
            Corrections = await db.Corrections.IgnoreQueryFilters().CountAsync(c => c.UserId == owner.Id),
            Lines = await db.SyncChanges.IgnoreQueryFilters().CountAsync(c => c.UserId == owner.Id),
        });
        left.Sessions.ShouldBe(0);
        left.People.ShouldBe(0);
        left.Corrections.ShouldBe(0);
        left.Lines.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-13")]
    public async Task a_delete_of_a_tenant_removes_the_journal_of_each_of_its_users()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = "Admin")
            .Under(tenant)
            .BuildAsync();
        scope.SetUserWithDefaults(identifier: admin.Identifier);
        var now = DateTimeOffset.UtcNow;
        await scope.InsertAsync(Session.Create(tenant.Id, admin.Id, Guid.NewGuid().ToString(), now, null, TimeZoneId.Utc, now));

        // Act
        await scope.SendAsync(new DeleteTenant.Command(tenant.Id));

        // Assert
        var left = await scope.ExecuteDbContextAsync(async db => new
        {
            Sessions = await db.Sessions.IgnoreQueryFilters().CountAsync(s => s.UserId == admin.Id),
            Lines = await db.SyncChanges.IgnoreQueryFilters().CountAsync(c => c.UserId == admin.Id),
        });
        left.Sessions.ShouldBe(0);
        left.Lines.ShouldBe(0);
    }

    private static readonly Type[] JournalTablesOutsideTheFeed =
        [typeof(SyncChange), typeof(Recording), typeof(Summary), typeof(ImportBatch), typeof(ImportEntry), typeof(ImportDay)];

    [Test]
    [Property("Rule", "IMP-2")]
    public async Task each_journal_table_of_the_model_has_its_triggers()
    {
        // Arrange
        var triggers = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var connection = new SqliteConnection(TestFixture.UnpooledConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                triggers[reader.GetString(0)] = reader.GetString(1);
        }

        var model = new TestingServiceScope().GetService<AppDbContext>().Model;
        var journalRoots = model.GetEntityTypes().Where(e => e.ClrType.IsAssignableTo(typeof(IJournalOwned))).ToList();
        var fed = journalRoots.Where(e => !JournalTablesOutsideTheFeed.Contains(e.ClrType)).Select(e => e.GetTableName()!).ToList();
        var guarded = journalRoots.Where(e => JournalTablesOutsideTheFeed.Contains(e.ClrType) && e.ClrType != typeof(SyncChange))
            .Select(e => e.GetTableName()!).ToList();
        var revisions = new[] { model.FindEntityType(typeof(RawRevision))!.GetTableName()!, model.FindEntityType(typeof(CleanedRevision))!.GetTableName()! };

        // Act
        var missing = fed.SelectMany(table => new[] { $"{table}_sync_insert", $"{table}_sync_update", $"{table}_sync_delete" })
            .Concat(guarded.Select(table => $"{table}_refuse_deleted_owner"))
            .Concat(revisions.SelectMany(table => new[] { $"{table}_refuse_update", $"{table}_refuse_delete" }))
            .Append("users_purge_journal")
            .Where(name => !triggers.ContainsKey(name))
            .ToList();

        // Assert
        missing.ShouldBeEmpty("An EF table rebuild drops the triggers of the table, so call JournalTriggers again in that migration.");
        triggers.Keys.Where(name => name.StartsWith("import_", StringComparison.Ordinal) && name.Contains("_sync_", StringComparison.Ordinal))
            .ShouldBeEmpty("An entry of an open batch is not in the Sync feed (IMP-2).");
        foreach (var table in journalRoots.Select(e => e.GetTableName()!))
            triggers["users_purge_journal"].ShouldContain($"DELETE FROM {table} WHERE user_id = new.id");
    }

    [Test]
    [Property("Rule", "IMP-15")]
    public async Task the_import_batch_of_a_session_has_an_index_and_no_foreign_key()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();

        async Task<List<string>> ColumnAsync(string sql, int column)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var rows = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await rows.ReadAsync())
                values.Add(rows.GetString(column));
            return values;
        }

        // Act
        var keyColumns = await ColumnAsync("PRAGMA foreign_key_list(sessions)", 3);
        var indexed = await ColumnAsync("SELECT name FROM pragma_index_info('ix_sessions_import_batch_id')", 0);

        // Assert
        keyColumns.ShouldNotContain("import_batch_id");
        indexed.ShouldBe(["import_batch_id"]);
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public async Task the_slot_lookup_of_a_put_has_its_index_from_the_migration()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_info('ix_import_entries_batch_id_date_position') ORDER BY seqno";

        // Act
        var columns = new List<string>();
        await using (var rows = await command.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
                columns.Add(rows.GetString(0));
        }

        // Assert
        columns.ShouldBe(["batch_id", "date", "position"],
            "The model does not hold this index, so an EF rebuild of import_entries drops it. Add it again in that migration.");
    }

    [Test]
    public async Task the_tombstone_lookup_of_a_create_uses_its_partial_index()
    {
        // Arrange
        await using var connection = new SqliteConnection(TestFixture.UnpooledConnectionString);
        await connection.OpenAsync();
        await using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN SELECT EXISTS (SELECT 1 FROM "sync_changes" AS "s"
            WHERE "s"."user_id" = @user AND "s"."is_tombstone" AND "s"."client_session_id" = @client)
            """;
        plan.Parameters.AddWithValue("@user", "usr_1");
        plan.Parameters.AddWithValue("@client", "device-1");

        // Act
        await using var rows = await plan.ExecuteReaderAsync();
        var details = new List<string>();
        while (await rows.ReadAsync())
            details.Add(rows.GetString(3));

        // Assert
        details.ShouldContain(d => d.Contains("ix_sync_changes_user_id_client_session_id"));
    }

    private static async Task<(TestingServiceScope Scope, SessionDto Session)> SavedSessionAsync(string words)
    {
        var scope = new TestingServiceScope();
        var created = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }));
        await scope.SendAsync(new SaveRawDraft.Command(created.Session.Id, new DraftSaveDto
        {
            Text = words,
            BaseRevision = 0,
            SavedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }));
        await scope.SendAsync(new SaveCleanedDraft.Command(created.Session.Id, new DraftSaveDto
        {
            Text = words,
            BaseRevision = 0,
            SavedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }));
        return (new TestingServiceScope(), created.Session);
    }
}
