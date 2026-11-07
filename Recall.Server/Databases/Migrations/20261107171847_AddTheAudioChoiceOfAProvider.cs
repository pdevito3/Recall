using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheAudioChoiceOfAProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "transcription_chosen_on",
                table: "model_providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transcription_model",
                table: "model_providers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "transcription_chosen_on",
                table: "model_providers");

            migrationBuilder.DropColumn(
                name: "transcription_model",
                table: "model_providers");
        }
    }
}
