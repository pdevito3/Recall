using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recordings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    client_recording_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    format = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    engine = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    problem = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    highest_stored_slice = table.Column<int>(type: "INTEGER", nullable: true),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    transcribed_samples = table.Column<long>(type: "INTEGER", nullable: false),
                    has_text = table.Column<bool>(type: "INTEGER", nullable: false),
                    attempts_without_progress = table.Column<int>(type: "INTEGER", nullable: false),
                    audio_kept_until = table.Column<string>(type: "TEXT", nullable: true),
                    audio_deleted_at = table.Column<string>(type: "TEXT", nullable: true),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recordings", x => x.id);
                    table.ForeignKey(
                        name: "fk_recordings_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_recordings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_recordings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "recording_slices",
                columns: table => new
                {
                    recording_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    index = table.Column<int>(type: "INTEGER", nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recording_slices", x => new { x.recording_id, x.index });
                    table.ForeignKey(
                        name: "fk_recording_slices_recordings_recording_id",
                        column: x => x.recording_id,
                        principalTable: "recordings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recordings_session_id",
                table: "recordings",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_recordings_tenant_id",
                table: "recordings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_recordings_user_id_client_recording_id",
                table: "recordings",
                columns: new[] { "user_id", "client_recording_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recordings_user_id_status",
                table: "recordings",
                columns: new[] { "user_id", "status" });

            migrationBuilder.AddOwnerPurgeWithRecordings();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER users_purge_journal;");
            migrationBuilder.AddOwnerPurge();

            migrationBuilder.DropTable(
                name: "recording_slices");

            migrationBuilder.DropTable(
                name: "recordings");
        }
    }
}
