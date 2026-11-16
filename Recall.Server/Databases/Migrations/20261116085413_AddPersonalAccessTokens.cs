using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_client_kind",
                table: "auth_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_refresh_state",
                table: "auth_sessions");

            migrationBuilder.AddColumn<string>(
                name: "grant",
                table: "auth_sessions",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_used_at",
                table: "auth_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "auth_sessions",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_client_kind",
                table: "auth_sessions",
                sql: "client_kind IN ('web', 'native', 'pat')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_grant",
                table: "auth_sessions",
                sql: "(client_kind = 'pat') = (\"grant\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_last_used_at",
                table: "auth_sessions",
                sql: "client_kind = 'pat' OR last_used_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_name",
                table: "auth_sessions",
                sql: "(client_kind = 'pat') = (name IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_refresh_state",
                table: "auth_sessions",
                sql: "(client_kind IN ('web', 'pat') AND refresh_family_hash IS NULL AND refresh_hash IS NULL) OR (client_kind = 'native' AND refresh_family_hash IS NOT NULL AND refresh_hash IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM auth_sessions WHERE client_kind = 'pat';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_client_kind",
                table: "auth_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_grant",
                table: "auth_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_last_used_at",
                table: "auth_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_name",
                table: "auth_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_sessions_refresh_state",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "grant",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "name",
                table: "auth_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_client_kind",
                table: "auth_sessions",
                sql: "client_kind IN ('web', 'native')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_sessions_refresh_state",
                table: "auth_sessions",
                sql: "(client_kind = 'web' AND refresh_family_hash IS NULL AND refresh_hash IS NULL) OR (client_kind = 'native' AND refresh_family_hash IS NOT NULL AND refresh_hash IS NOT NULL)");
        }
    }
}
