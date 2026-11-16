using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddWriteNumbersAsDigitsToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "write_numbers_as_digits",
                table: "user_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, because a rebuild of the table breaks the triggers that name it.
            migrationBuilder.Sql("ALTER TABLE user_settings DROP COLUMN write_numbers_as_digits;");
        }
    }
}
