using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddTheCleanedBaseOfACleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cleanup_base_cleaned_revision",
                table: "sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "waiting_cleanup_revision",
                table: "sessions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, as main does, because a rebuild of sessions drops its triggers.
            migrationBuilder.Sql("ALTER TABLE sessions DROP COLUMN cleanup_base_cleaned_revision;");
            migrationBuilder.Sql("ALTER TABLE sessions DROP COLUMN waiting_cleanup_revision;");
        }
    }
}
