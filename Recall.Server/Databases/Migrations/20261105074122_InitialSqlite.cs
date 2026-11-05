using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    record_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    occurred_at = table.Column<string>(type: "TEXT", nullable: false),
                    actor_subject = table.Column<string>(type: "TEXT", nullable: true),
                    actor_client = table.Column<string>(type: "TEXT", nullable: true),
                    actor_user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true, collation: "BINARY"),
                    actor_agent_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true, collation: "BINARY"),
                    request_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    trace_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 32, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    root_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    root_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => new { x.occurred_at, x.record_id, x.ordinal });
                    table.CheckConstraint("ck_audit_entries_action_names_an_action", "(kind = 'Action') = (action IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "used_dpop_proofs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    expires = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_used_dpop_proofs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    client_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "BINARY"),
                    mode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    first_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    last_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    identifier = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "agent_permissions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    agent_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    permission = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_permissions_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_permissions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    permission = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_permissions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_permissions_agent_id",
                table: "agent_permissions",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_client_id",
                table: "agents",
                column: "client_id",
                unique: true,
                filter: "mode = 'machine' AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_agents_tenant_id_client_id",
                table: "agents",
                columns: new[] { "tenant_id", "client_id" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_agents_tenant_id_name_id",
                table: "agents",
                columns: new[] { "tenant_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_root_id_occurred_at_record_id_ordinal",
                table: "audit_entries",
                columns: new[] { "root_id", "occurred_at", "record_id", "ordinal" },
                descending: new[] { false, true, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_occurred_at_record_id_ordinal",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at", "record_id", "ordinal" },
                descending: new[] { false, true, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_name",
                table: "tenants",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_used_dpop_proofs_expires",
                table: "used_dpop_proofs",
                column: "expires");

            migrationBuilder.CreateIndex(
                name: "ix_user_permissions_user_id",
                table: "user_permissions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_identifier",
                table: "users",
                column: "identifier",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_last_name_first_name_id",
                table: "users",
                columns: new[] { "tenant_id", "last_name", "first_name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "users",
                column: "username");

            // The audit log is append-only: the database refuses a change of an entry, also from raw SQL.
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_entries_refuse_update BEFORE UPDATE ON audit_entries
                BEGIN
                    SELECT RAISE(ABORT, 'audit_entries is append-only, so UPDATE is not allowed.');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_entries_refuse_delete BEFORE DELETE ON audit_entries
                BEGIN
                    SELECT RAISE(ABORT, 'audit_entries is append-only, so DELETE is not allowed.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_permissions");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "used_dpop_proofs");

            migrationBuilder.DropTable(
                name: "user_permissions");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
