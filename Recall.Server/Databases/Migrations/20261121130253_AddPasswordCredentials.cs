using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "user_id",
                table: "auth_login_attempts",
                type: "TEXT",
                maxLength: 50,
                nullable: true,
                collation: "BINARY");

            migrationBuilder.CreateTable(
                name: "auth_password_credentials",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    login_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "BINARY"),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    changed_at = table.Column<string>(type: "TEXT", nullable: false),
                    failed_count = table.Column<int>(type: "INTEGER", nullable: false),
                    temporary_until = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_password_credentials", x => x.user_id);
                    table.CheckConstraint("ck_auth_password_credentials_temporary_until", "temporary_until IS NULL OR temporary_until <= strftime('%Y-%m-%dT%H:%M:%fZ', changed_at, '+7 days')");
                    table.ForeignKey(
                        name: "fk_auth_password_credentials_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_password_credentials_login_name",
                table: "auth_password_credentials",
                column: "login_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_password_credentials");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "auth_login_attempts");
        }
    }
}
