using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDPoPProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "used_dpop_proofs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "used_dpop_proofs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    expires = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_used_dpop_proofs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_used_dpop_proofs_expires",
                table: "used_dpop_proofs",
                column: "expires");
        }
    }
}
