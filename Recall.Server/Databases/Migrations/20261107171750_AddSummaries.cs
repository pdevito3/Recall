using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "summaries",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    period = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    start_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    run_state = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    failure = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    content = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    source_count = table.Column<int>(type: "INTEGER", nullable: false),
                    source_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    generated_at = table.Column<string>(type: "TEXT", nullable: true),
                    run_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true, collation: "BINARY"),
                    run_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_summaries", x => x.id);
                    table.ForeignKey(
                        name: "fk_summaries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_summaries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_summaries_tenant_id",
                table: "summaries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_summaries_user_id_period_start_date",
                table: "summaries",
                columns: new[] { "user_id", "period", "start_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_summaries_user_id_run_job_id",
                table: "summaries",
                columns: new[] { "user_id", "run_job_id" });

            migrationBuilder.AddOwnerGuard("summaries");
            migrationBuilder.AddOwnerPurgeWithSummaries();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddOwnerPurgeWithRecordings();

            migrationBuilder.DropTable(
                name: "summaries");
        }
    }
}
