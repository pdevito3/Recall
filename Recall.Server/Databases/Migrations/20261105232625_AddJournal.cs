using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    canonical_term = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    mishearings = table.Column<string>(type: "TEXT", maxLength: 120300, nullable: false),
                    hard_replace = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_corrections", x => x.id);
                    table.ForeignKey(
                        name: "fk_corrections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_corrections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_people_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_people_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    client_session_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    started_at = table.Column<string>(type: "TEXT", nullable: false),
                    preview = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    cleaned_has_hand_edits = table.Column<bool>(type: "INTEGER", nullable: false),
                    synopsis = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    cleanup_run = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    last_cleanup_raw_revision = table.Column<int>(type: "INTEGER", nullable: true),
                    metadata_saved_at = table.Column<string>(type: "TEXT", nullable: true),
                    metadata_fast_claims = table.Column<string>(type: "TEXT", nullable: false),
                    activity = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    moods = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    user_topics = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    ai_topics = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    mentioned_person_ids = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    cleaned_draft_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    cleaned_latest_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    cleaned_saved_at = table.Column<string>(type: "TEXT", nullable: true),
                    cleaned_text = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false),
                    raw_draft_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    raw_latest_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    raw_saved_at = table.Column<string>(type: "TEXT", nullable: true),
                    raw_text = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sync_changes",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    client_session_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    is_tombstone = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_changes", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    location_capture_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    require_people_proposal_approval = table.Column<bool>(type: "INTEGER", nullable: false),
                    saved_at = table.Column<string>(type: "TEXT", nullable: true),
                    fast_claims = table.Column<string>(type: "TEXT", nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_user_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "cleaned_revisions",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    saved_at = table.Column<string>(type: "TEXT", nullable: false),
                    claimed_saved_at = table.Column<string>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cleaned_revisions", x => new { x.session_id, x.number });
                    table.ForeignKey(
                        name: "fk_cleaned_revisions_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "raw_revisions",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    saved_at = table.Column<string>(type: "TEXT", nullable: false),
                    claimed_saved_at = table.Column<string>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_revisions", x => new { x.session_id, x.number });
                    table.ForeignKey(
                        name: "fk_raw_revisions_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_corrections_tenant_id",
                table: "corrections",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_user_id_canonical_term_id",
                table: "corrections",
                columns: new[] { "user_id", "canonical_term", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_people_tenant_id",
                table: "people",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_people_user_id_label_id",
                table: "people",
                columns: new[] { "user_id", "label", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_tenant_id",
                table: "sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_client_session_id",
                table: "sessions",
                columns: new[] { "user_id", "client_session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_started_at_id",
                table: "sessions",
                columns: new[] { "user_id", "started_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_sync_changes_entity_type_entity_id",
                table: "sync_changes",
                columns: new[] { "entity_type", "entity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_changes_user_id_client_session_id",
                table: "sync_changes",
                columns: new[] { "user_id", "client_session_id" },
                filter: "is_tombstone AND client_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sync_changes_user_id_sequence",
                table: "sync_changes",
                columns: new[] { "user_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_tenant_id",
                table: "user_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_user_id",
                table: "user_settings",
                column: "user_id",
                unique: true);

            migrationBuilder.AddSyncFeed("sessions", "Session", IdsMadeBy.Server, clientSessionIdColumn: "client_session_id");
            migrationBuilder.AddSyncFeed("people", "Person", IdsMadeBy.Server);
            migrationBuilder.AddSyncFeed("corrections", "Correction", IdsMadeBy.Server);
            migrationBuilder.AddSyncFeed("user_settings", "UserSettings", IdsMadeBy.Server);
            migrationBuilder.AddAppendOnlyGuards("raw_revisions");
            migrationBuilder.AddAppendOnlyGuards("cleaned_revisions");
            migrationBuilder.AddOwnerPurge();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER users_purge_journal;");

            migrationBuilder.DropTable(
                name: "cleaned_revisions");

            migrationBuilder.DropTable(
                name: "corrections");

            migrationBuilder.DropTable(
                name: "people");

            migrationBuilder.DropTable(
                name: "raw_revisions");

            migrationBuilder.DropTable(
                name: "sync_changes");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
