using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheChangeIdsOfDraftSaves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "change_id",
                table: "raw_revisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_change_id",
                table: "raw_revisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "change_id",
                table: "cleaned_revisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_change_id",
                table: "cleaned_revisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "change_id",
                table: "raw_revisions");

            migrationBuilder.DropColumn(
                name: "parent_change_id",
                table: "raw_revisions");

            migrationBuilder.DropColumn(
                name: "change_id",
                table: "cleaned_revisions");

            migrationBuilder.DropColumn(
                name: "parent_change_id",
                table: "cleaned_revisions");
        }
    }
}
