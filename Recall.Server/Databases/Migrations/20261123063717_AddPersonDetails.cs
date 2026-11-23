using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "full_name",
                table: "people",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "people",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        // EF drops a column with a table rebuild, and a rebuild loses the triggers of a journal table (ADR 0020). SQLite drops
        // these columns in place, because no trigger or index reads them.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE people DROP COLUMN full_name;");
            migrationBuilder.Sql("ALTER TABLE people DROP COLUMN notes;");
        }
    }
}
