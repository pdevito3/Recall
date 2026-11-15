using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoveFillerWordsToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "remove_filler_words",
                table: "user_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, because a rebuild of the table breaks the triggers that name it.
            migrationBuilder.Sql("ALTER TABLE user_settings DROP COLUMN remove_filler_words;");
        }
    }
}
