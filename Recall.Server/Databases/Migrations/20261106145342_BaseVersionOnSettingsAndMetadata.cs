using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class BaseVersionOnSettingsAndMetadata : Migration
    {
        // EF drops a column with a table rebuild, and a rebuild loses the triggers of a journal table (ADR 0020). SQLite drops
        // these columns in place, because no trigger or index reads them.
        private static void DropColumnInPlace(MigrationBuilder migrationBuilder, string table, string column)
            => migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN {column};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropColumnInPlace(migrationBuilder, "user_settings", "fast_claims");
            DropColumnInPlace(migrationBuilder, "user_settings", "saved_at");
            DropColumnInPlace(migrationBuilder, "sessions", "metadata_fast_claims");
            DropColumnInPlace(migrationBuilder, "sessions", "metadata_saved_at");

            migrationBuilder.AddColumn<uint>(
                name: "metadata_version",
                table: "sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropColumnInPlace(migrationBuilder, "sessions", "metadata_version");

            migrationBuilder.AddColumn<string>(
                name: "fast_claims",
                table: "user_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "saved_at",
                table: "user_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_fast_claims",
                table: "sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "metadata_saved_at",
                table: "sessions",
                type: "TEXT",
                nullable: true);
        }
    }
}
