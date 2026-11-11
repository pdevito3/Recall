using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheAudioFileOfARecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_file",
                table: "recordings",
                type: "TEXT",
                nullable: true);

            // A Recording that has joined audio from before this column has it under the name of its download.
            migrationBuilder.Sql("""
                UPDATE recordings
                SET audio_file = CASE format WHEN 'Mp4' THEN 'audio.mp4' ELSE 'audio.webm' END
                WHERE (status <> 'Receiving' OR quiet_end_through_slice >= 0) AND problem <> 'NoAudioReceived';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, as main does, because a rebuild of recordings breaks the triggers that name it.
            migrationBuilder.Sql("ALTER TABLE recordings DROP COLUMN audio_file;");
        }
    }
}
