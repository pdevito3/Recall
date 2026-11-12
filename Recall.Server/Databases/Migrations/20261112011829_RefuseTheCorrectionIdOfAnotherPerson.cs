using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class RefuseTheCorrectionIdOfAnotherPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropSyncFeed(migrationBuilder);
            migrationBuilder.AddSyncFeed("corrections", "Correction", IdsMadeBy.Device);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropSyncFeed(migrationBuilder);
            migrationBuilder.AddSyncFeed("corrections", "Correction", IdsMadeBy.Server);
        }

        private static void DropSyncFeed(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER corrections_sync_insert;");
            migrationBuilder.Sql("DROP TRIGGER corrections_sync_update;");
            migrationBuilder.Sql("DROP TRIGGER corrections_sync_delete;");
        }
    }
}
