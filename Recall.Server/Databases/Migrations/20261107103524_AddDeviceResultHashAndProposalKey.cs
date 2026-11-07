using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceResultHashAndProposalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "proposal_key",
                table: "people",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "device_result_hash",
                table: "cleaned_revisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_people_user_id_proposal_key",
                table: "people",
                columns: new[] { "user_id", "proposal_key" },
                unique: true,
                filter: "proposal_key IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_people_user_id_proposal_key",
                table: "people");

            migrationBuilder.DropColumn(
                name: "proposal_key",
                table: "people");

            migrationBuilder.DropColumn(
                name: "device_result_hash",
                table: "cleaned_revisions");
        }
    }
}
