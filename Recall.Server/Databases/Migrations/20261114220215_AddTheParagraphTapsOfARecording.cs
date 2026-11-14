using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheParagraphTapsOfARecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "next_words_start_paragraph",
                table: "recordings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "paragraph_taps_ms",
                table: "recordings",
                type: "TEXT",
                maxLength: 9002,
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, as main does, because a rebuild of recordings breaks the triggers that name it.
            migrationBuilder.Sql("ALTER TABLE recordings DROP COLUMN next_words_start_paragraph;");
            migrationBuilder.Sql("ALTER TABLE recordings DROP COLUMN paragraph_taps_ms;");
        }
    }
}
