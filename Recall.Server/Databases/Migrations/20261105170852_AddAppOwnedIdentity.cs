using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddAppOwnedIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_app_codes",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    login_mode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    redirect_uri = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    challenge = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    client_state = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    expires_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_app_codes", x => x.code_hash);
                    table.ForeignKey(
                        name: "fk_auth_app_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "auth_external_logins",
                columns: table => new
                {
                    issuer = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, collation: "BINARY"),
                    subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_login_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_external_logins", x => new { x.issuer, x.subject });
                    table.ForeignKey(
                        name: "fk_auth_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "auth_login_attempts",
                columns: table => new
                {
                    state_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    binding_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    mode_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    pkce_verifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    redirect_uri = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    client_kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    return_path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    native_redirect = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    native_challenge = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    native_state = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    expires_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_login_attempts", x => x.state_hash);
                    table.CheckConstraint("ck_auth_login_attempts_client_kind", "client_kind IN ('web', 'native')");
                });

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    client_kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    login_mode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    access_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    access_expires_at = table.Column<string>(type: "TEXT", nullable: false),
                    refresh_family_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, collation: "BINARY"),
                    refresh_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, collation: "BINARY"),
                    expires_at = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_slid_at = table.Column<string>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<string>(type: "TEXT", nullable: true),
                    revoked_reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                    table.CheckConstraint("ck_auth_sessions_client_kind", "client_kind IN ('web', 'native')");
                    table.CheckConstraint("ck_auth_sessions_refresh_state", "(client_kind = 'web' AND refresh_family_hash IS NULL AND refresh_hash IS NULL) OR (client_kind = 'native' AND refresh_family_hash IS NOT NULL AND refresh_hash IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_auth_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_app_codes_expires_at",
                table: "auth_app_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_app_codes_user_id",
                table: "auth_app_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_external_logins_user_id",
                table: "auth_external_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_login_attempts_expires_at",
                table: "auth_login_attempts",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_sweep",
                table: "auth_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id",
                table: "auth_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_auth_sessions_access_hash",
                table: "auth_sessions",
                column: "access_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_auth_sessions_refresh_family",
                table: "auth_sessions",
                column: "refresh_family_hash",
                unique: true,
                filter: "refresh_family_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_app_codes");

            migrationBuilder.DropTable(
                name: "auth_external_logins");

            migrationBuilder.DropTable(
                name: "auth_login_attempts");

            migrationBuilder.DropTable(
                name: "auth_sessions");
        }
    }
}
