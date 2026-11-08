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

        // EF drops a column with a table rebuild, and a rebuild loses the triggers of a journal table (ADR 0020). SQLite drops
        // these columns in place, because no trigger or index reads them.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "raw_revisions", "cleaned_revisions" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN change_id;");
                migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN parent_change_id;");
            }
        }
    }
}
