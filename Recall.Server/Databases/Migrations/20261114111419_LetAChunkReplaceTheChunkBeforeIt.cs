using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class LetAChunkReplaceTheChunkBeforeIt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "from_chunk",
                table: "raw_revisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            migrationBuilder.LetAChunkRevisionGo();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF drops a column with a table rebuild, and a rebuild loses the triggers of a journal table (ADR 0020). SQLite
            // drops this column in place once the delete guard no longer reads it.
            migrationBuilder.KeepEachRevisionOfRaw();
            migrationBuilder.Sql("ALTER TABLE raw_revisions DROP COLUMN from_chunk;");
        }
    }
}
