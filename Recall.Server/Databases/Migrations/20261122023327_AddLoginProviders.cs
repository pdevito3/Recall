using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_oidc_providers",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "BINARY"),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    issuer = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, collation: "BINARY"),
                    client_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    protected_client_secret = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    allow_http = table.Column<bool>(type: "INTEGER", nullable: false),
                    ca_certificate_path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_oidc_providers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_oidc_providers_created_at",
                table: "auth_oidc_providers",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_oidc_providers_id",
                table: "auth_oidc_providers",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_oidc_providers_issuer",
                table: "auth_oidc_providers",
                column: "issuer",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_oidc_providers");
        }
    }
}
