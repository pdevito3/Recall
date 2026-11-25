using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddImportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "require_import_review",
                table: "user_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "import_batch_id",
                table: "sessions",
                type: "TEXT",
                maxLength: 50,
                nullable: true,
                collation: "BINARY");

            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    opened_at = table.Column<string>(type: "TEXT", nullable: false),
                    accepted_at = table.Column<string>(type: "TEXT", nullable: true),
                    days_rejected = table.Column<int>(type: "INTEGER", nullable: false),
                    entries_rejected = table.Column<int>(type: "INTEGER", nullable: false),
                    sessions_made = table.Column<int>(type: "INTEGER", nullable: false),
                    skipped_deleted = table.Column<int>(type: "INTEGER", nullable: false),
                    skipped_in_journal = table.Column<int>(type: "INTEGER", nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_import_batches_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "import_days",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    batch_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    rejected = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_days_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_days_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "import_entries",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    batch_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    client_session_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    words_changed_after_edit = table.Column<bool>(type: "INTEGER", nullable: false),
                    time_changed_after_edit = table.Column<bool>(type: "INTEGER", nullable: false),
                    date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    moods = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    raw = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false),
                    time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    topics = table.Column<string>(type: "TEXT", maxLength: 30150, nullable: false),
                    review_rejected = table.Column<bool>(type: "INTEGER", nullable: false),
                    review_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    review_words = table.Column<string>(type: "TEXT", maxLength: 4000000, nullable: true),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_entries_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_import_batch_id",
                table: "sessions",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_tenant_id",
                table: "import_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_user_id_opened_at",
                table: "import_batches",
                columns: new[] { "user_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_days_batch_id_date",
                table: "import_days",
                columns: new[] { "batch_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_days_user_id",
                table: "import_days",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_entries_batch_id",
                table: "import_entries",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_entries_user_id_client_session_id",
                table: "import_entries",
                columns: new[] { "user_id", "client_session_id" },
                unique: true);

            // EF Core 10 cannot index a column of the complex property Agent, so the model does not hold this index.
            migrationBuilder.CreateIndex(
                name: "ix_import_entries_batch_id_date_position",
                table: "import_entries",
                columns: new[] { "batch_id", "date", "position" });

            migrationBuilder.AddOwnerGuard("import_batches");
            migrationBuilder.AddOwnerGuard("import_entries");
            migrationBuilder.AddOwnerGuard("import_days");
            migrationBuilder.AddOwnerPurgeWithImports();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddOwnerPurgeWithSummaries();

            migrationBuilder.DropTable(
                name: "import_days");

            migrationBuilder.DropTable(
                name: "import_entries");

            migrationBuilder.DropTable(
                name: "import_batches");

            // In place, because a rebuild of the table breaks the triggers that name it.
            migrationBuilder.Sql("DROP INDEX ix_sessions_import_batch_id;");
            migrationBuilder.Sql("ALTER TABLE sessions DROP COLUMN import_batch_id;");
            migrationBuilder.Sql("ALTER TABLE user_settings DROP COLUMN require_import_review;");
        }
    }
}
