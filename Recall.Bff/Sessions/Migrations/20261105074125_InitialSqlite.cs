using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Bff.Sessions.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    friendly_name = table.Column<string>(type: "TEXT", nullable: true),
                    xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false, collation: "BINARY"),
                    subject_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    created = table.Column<string>(type: "TEXT", nullable: false),
                    renewed = table.Column<string>(type: "TEXT", nullable: false),
                    expires = table.Column<string>(type: "TEXT", nullable: false),
                    absolute_expires = table.Column<string>(type: "TEXT", nullable: false),
                    ticket = table.Column<byte[]>(type: "BLOB", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "used_logout_tokens",
                columns: table => new
                {
                    jti = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "BINARY"),
                    expires = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_used_logout_tokens", x => x.jti);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_absolute_expires",
                table: "sessions",
                column: "absolute_expires");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_expires",
                table: "sessions",
                column: "expires");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_session_id",
                table: "sessions",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_subject_id",
                table: "sessions",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_used_logout_tokens_expires",
                table: "used_logout_tokens",
                column: "expires");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "used_logout_tokens");
        }
    }
}
