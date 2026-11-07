using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTheClockFromDraftSaves : Migration
    {
        // EF drops a column with a table rebuild, and a rebuild loses the triggers of a journal table (ADR 0020). SQLite drops
        // these columns in place, because no trigger or index reads them.
        private static void DropColumnInPlace(MigrationBuilder migrationBuilder, string table, string column)
            => migrationBuilder.Sql($"ALTER TABLE {table} DROP COLUMN {column};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropColumnInPlace(migrationBuilder, "sessions", "last_transcript_revision");

            foreach (var table in new[] { "raw_revisions", "cleaned_revisions" })
            {
                DropColumnInPlace(migrationBuilder, table, "saved_at");
                migrationBuilder.RenameColumn(name: "claimed_saved_at", table: table, newName: "saved_at");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "raw_revisions", "cleaned_revisions" })
            {
                migrationBuilder.RenameColumn(name: "saved_at", table: table, newName: "claimed_saved_at");
                migrationBuilder.AddColumn<DateTimeOffset>(
                    name: "saved_at",
                    table: table,
                    type: "TEXT",
                    nullable: false,
                    defaultValue: DateTimeOffset.UnixEpoch);
            }

            migrationBuilder.AddColumn<int>(
                name: "last_transcript_revision",
                table: "sessions",
                type: "INTEGER",
                nullable: true);
        }
    }
}
