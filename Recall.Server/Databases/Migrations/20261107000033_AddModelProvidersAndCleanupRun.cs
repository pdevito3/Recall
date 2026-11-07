using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProvidersAndCleanupRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cleanup_failure",
                table: "sessions",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "cleanup_run_id",
                table: "sessions",
                type: "TEXT",
                maxLength: 50,
                nullable: true,
                collation: "BINARY");

            migrationBuilder.AddColumn<string>(
                name: "mood_suggestions",
                table: "sessions",
                type: "TEXT",
                maxLength: 30150,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "people_proposals",
                table: "sessions",
                type: "TEXT",
                maxLength: 13000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "topic_suggestions",
                table: "sessions",
                type: "TEXT",
                maxLength: 30150,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "key_ring_markers",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    protected_value = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_ring_markers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_providers",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    base_url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    protected_api_key = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    activated_on = table.Column<string>(type: "TEXT", nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_providers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_providers_tenant_id_activated_on",
                table: "model_providers",
                columns: new[] { "tenant_id", "activated_on" });

            migrationBuilder.GrantPermissionToRole("Admin", "manage_model_providers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "key_ring_markers");

            migrationBuilder.DropTable(
                name: "model_providers");

            migrationBuilder.DropColumn(
                name: "cleanup_failure",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "cleanup_run_id",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "mood_suggestions",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "people_proposals",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "topic_suggestions",
                table: "sessions");
        }
    }
}
