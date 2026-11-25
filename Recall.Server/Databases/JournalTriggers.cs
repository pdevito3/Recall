namespace Recall.Server.Databases;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// An EF table rebuild drops the triggers of the table, so a migration that rebuilds a journal table must call these
/// again.
/// </summary>
public static class JournalTriggers
{
    public const string OwnerDeletedMessage = "The owner of this row is deleted.";
    public const string IdTakenMessage = "This ID was deleted or another person uses it, so it cannot be used. Drop the local copy.";

    public static void AddSyncFeed(
        this MigrationBuilder migrationBuilder,
        string table,
        string entityType,
        IdsMadeBy idsMadeBy,
        string? clientSessionIdColumn = null)
    {
        string ClientId(string row) => clientSessionIdColumn is null ? "NULL" : $"{row}.{clientSessionIdColumn}";
        var refuseATakenId = idsMadeBy == IdsMadeBy.Device
            ? $"SELECT RAISE(ABORT, '{IdTakenMessage}') WHERE EXISTS (SELECT 1 FROM sync_changes WHERE entity_type = '{entityType}' AND entity_id = new.id AND (user_id <> new.user_id OR is_tombstone));"
            : string.Empty;

        migrationBuilder.Sql($"""
            CREATE TRIGGER {table}_sync_insert BEFORE INSERT ON {table}
            BEGIN
                SELECT RAISE(ABORT, '{OwnerDeletedMessage}') WHERE (SELECT is_deleted FROM users WHERE id = new.user_id) = 1;
                {refuseATakenId}
                INSERT OR REPLACE INTO sync_changes (user_id, entity_type, entity_id, client_session_id, is_tombstone)
                VALUES (new.user_id, '{entityType}', new.id, {ClientId("new")}, 0);
            END;
            """);
        migrationBuilder.Sql($"""
            CREATE TRIGGER {table}_sync_update BEFORE UPDATE ON {table}
            BEGIN
                INSERT OR REPLACE INTO sync_changes (user_id, entity_type, entity_id, client_session_id, is_tombstone)
                VALUES (new.user_id, '{entityType}', new.id, {ClientId("new")}, 0);
            END;
            """);
        migrationBuilder.Sql($"""
            CREATE TRIGGER {table}_sync_delete BEFORE DELETE ON {table}
            BEGIN
                INSERT OR REPLACE INTO sync_changes (user_id, entity_type, entity_id, client_session_id, is_tombstone)
                VALUES (old.user_id, '{entityType}', old.id, {ClientId("old")}, 1);
            END;
            """);
    }

    public static void AddAppendOnlyGuards(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            CREATE TRIGGER {table}_refuse_update BEFORE UPDATE ON {table}
            BEGIN
                SELECT RAISE(ABORT, '{table} is append-only, so UPDATE is not allowed.');
            END;
            """);
        migrationBuilder.Sql($"""
            CREATE TRIGGER {table}_refuse_delete BEFORE DELETE ON {table}
            WHEN EXISTS (SELECT 1 FROM sessions WHERE id = old.session_id)
            BEGIN
                SELECT RAISE(ABORT, 'A Revision goes only with its Session.');
            END;
            """);
    }

    /// <summary>
    /// A Revision of Raw that a chunk made can go while its Session stays, because the next chunk holds each of its words
    /// (SES-48). A rebuild of <c>raw_revisions</c> calls <see cref="AddAppendOnlyGuards"/> and then this.
    /// </summary>
    public static void LetAChunkRevisionGo(this MigrationBuilder migrationBuilder)
        => ReplaceTheDeleteGuardOfRaw(migrationBuilder, "old.from_chunk = 0 AND ");

    public static void KeepEachRevisionOfRaw(this MigrationBuilder migrationBuilder)
        => ReplaceTheDeleteGuardOfRaw(migrationBuilder, string.Empty);

    private static void ReplaceTheDeleteGuardOfRaw(MigrationBuilder migrationBuilder, string kept)
    {
        migrationBuilder.Sql("DROP TRIGGER raw_revisions_refuse_delete;");
        migrationBuilder.Sql($"""
            CREATE TRIGGER raw_revisions_refuse_delete BEFORE DELETE ON raw_revisions
            WHEN {kept}EXISTS (SELECT 1 FROM sessions WHERE id = old.session_id)
            BEGIN
                SELECT RAISE(ABORT, 'A Revision goes only with its Session.');
            END;
            """);
    }

    public static void AddOwnerGuard(this MigrationBuilder migrationBuilder, string table) => migrationBuilder.Sql($"""
        CREATE TRIGGER {table}_refuse_deleted_owner BEFORE INSERT ON {table}
        BEGIN
            SELECT RAISE(ABORT, '{OwnerDeletedMessage}') WHERE (SELECT is_deleted FROM users WHERE id = new.user_id) = 1;
        END;
        """);

    public static void AddOwnerPurgeWithRecordings(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER users_purge_journal;");
        migrationBuilder.Sql("""
            CREATE TRIGGER users_purge_journal BEFORE UPDATE OF is_deleted ON users
            WHEN new.is_deleted = 1 AND old.is_deleted = 0
            BEGIN
                DELETE FROM recordings WHERE user_id = new.id;
                DELETE FROM sessions WHERE user_id = new.id;
                DELETE FROM people WHERE user_id = new.id;
                DELETE FROM corrections WHERE user_id = new.id;
                DELETE FROM user_settings WHERE user_id = new.id;
                DELETE FROM sync_changes WHERE user_id = new.id;
            END;
            """);
    }

    public static void AddOwnerPurgeWithSummaries(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER users_purge_journal;");
        migrationBuilder.Sql("""
            CREATE TRIGGER users_purge_journal BEFORE UPDATE OF is_deleted ON users
            WHEN new.is_deleted = 1 AND old.is_deleted = 0
            BEGIN
                DELETE FROM summaries WHERE user_id = new.id;
                DELETE FROM recordings WHERE user_id = new.id;
                DELETE FROM sessions WHERE user_id = new.id;
                DELETE FROM people WHERE user_id = new.id;
                DELETE FROM corrections WHERE user_id = new.id;
                DELETE FROM user_settings WHERE user_id = new.id;
                DELETE FROM sync_changes WHERE user_id = new.id;
            END;
            """);
    }

    public static void AddOwnerPurgeWithImports(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER users_purge_journal;");
        migrationBuilder.Sql("""
            CREATE TRIGGER users_purge_journal BEFORE UPDATE OF is_deleted ON users
            WHEN new.is_deleted = 1 AND old.is_deleted = 0
            BEGIN
                DELETE FROM import_entries WHERE user_id = new.id;
                DELETE FROM import_days WHERE user_id = new.id;
                DELETE FROM import_batches WHERE user_id = new.id;
                DELETE FROM summaries WHERE user_id = new.id;
                DELETE FROM recordings WHERE user_id = new.id;
                DELETE FROM sessions WHERE user_id = new.id;
                DELETE FROM people WHERE user_id = new.id;
                DELETE FROM corrections WHERE user_id = new.id;
                DELETE FROM user_settings WHERE user_id = new.id;
                DELETE FROM sync_changes WHERE user_id = new.id;
            END;
            """);
    }

    public static void AddOwnerPurge(this MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TRIGGER users_purge_journal BEFORE UPDATE OF is_deleted ON users
        WHEN new.is_deleted = 1 AND old.is_deleted = 0
        BEGIN
            DELETE FROM sessions WHERE user_id = new.id;
            DELETE FROM people WHERE user_id = new.id;
            DELETE FROM corrections WHERE user_id = new.id;
            DELETE FROM user_settings WHERE user_id = new.id;
            DELETE FROM sync_changes WHERE user_id = new.id;
        END;
        """);
}

public enum IdsMadeBy
{
    Server,
    Device,
}
