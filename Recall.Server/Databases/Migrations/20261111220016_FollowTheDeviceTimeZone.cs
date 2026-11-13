using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class FollowTheDeviceTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone_mode",
                table: "user_settings",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "FollowDevice");

            migrationBuilder.Sql("UPDATE user_settings SET time_zone_mode = 'Fixed' WHERE time_zone IS NOT NULL;");

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                table: "sessions",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE sessions SET time_zone = COALESCE(
                    (SELECT s.time_zone FROM user_settings s WHERE s.user_id = sessions.user_id),
                    'UTC');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, because a rebuild of either table breaks the triggers that name it.
            migrationBuilder.Sql("ALTER TABLE user_settings DROP COLUMN time_zone_mode;");
            migrationBuilder.Sql("ALTER TABLE sessions DROP COLUMN time_zone;");
        }
    }
}
