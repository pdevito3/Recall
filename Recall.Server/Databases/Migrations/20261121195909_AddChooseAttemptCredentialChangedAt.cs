using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddChooseAttemptCredentialChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "credential_changed_at",
                table: "auth_login_attempts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite drops the column in place. DropColumn rebuilds the table, and the rebuild orders its columns by name.
            migrationBuilder.Sql("ALTER TABLE auth_login_attempts DROP COLUMN credential_changed_at;");
        }
    }
}
