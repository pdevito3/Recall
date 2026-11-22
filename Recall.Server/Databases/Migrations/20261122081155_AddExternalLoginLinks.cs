using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalLoginLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "link_session_id",
                table: "auth_login_attempts",
                type: "TEXT",
                maxLength: 50,
                nullable: true,
                collation: "BINARY");

            migrationBuilder.AddColumn<string>(
                name: "display_label",
                table: "auth_external_logins",
                type: "TEXT",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite drops the column in place. DropColumn rebuilds the table, and the rebuild orders its columns by name.
            migrationBuilder.Sql("ALTER TABLE auth_login_attempts DROP COLUMN link_session_id;");
            migrationBuilder.Sql("ALTER TABLE auth_external_logins DROP COLUMN display_label;");
        }
    }
}
