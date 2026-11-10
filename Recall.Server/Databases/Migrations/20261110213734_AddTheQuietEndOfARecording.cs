using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheQuietEndOfARecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "quiet_end_through_slice",
                table: "recordings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quiet_end_through_slice",
                table: "recordings");
        }
    }
}
