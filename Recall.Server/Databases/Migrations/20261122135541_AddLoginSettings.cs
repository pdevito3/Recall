using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_login_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    password_login = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_login_settings", x => x.id);
                    table.CheckConstraint("ck_auth_login_settings_one_row", "id = 1");
                    table.CheckConstraint("ck_auth_login_settings_password_login", "password_login IN ('On', 'Off')");
                });

            migrationBuilder.InsertData(
                table: "auth_login_settings",
                columns: new[] { "id", "password_login", "version" },
                values: new object[] { 1, "On", 1u });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_login_settings");
        }
    }
}
