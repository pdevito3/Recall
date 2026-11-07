using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class MakeModelProviderActivationNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "activated_on",
                table: "model_providers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        // EF rebuilds a table from the model of the migration before, and that migration came from main without model_providers, so
        // EF cannot rebuild it. This rebuild is written out. No trigger and no foreign key reads model_providers.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_model_providers" (
                    "id" TEXT COLLATE BINARY NOT NULL CONSTRAINT "pk_model_providers" PRIMARY KEY,
                    "activated_on" TEXT NOT NULL,
                    "base_url" TEXT NULL,
                    "is_deleted" INTEGER NOT NULL,
                    "kind" TEXT NOT NULL,
                    "last_modified_on" TEXT NULL,
                    "model" TEXT NULL,
                    "name" TEXT NOT NULL,
                    "protected_api_key" TEXT NULL,
                    "tenant_id" TEXT COLLATE BINARY NOT NULL,
                    "version" INTEGER NOT NULL,
                    CONSTRAINT "fk_model_providers_tenants_tenant_id" FOREIGN KEY ("tenant_id") REFERENCES "tenants" ("id")
                );
                INSERT INTO "ef_temp_model_providers" ("id", "activated_on", "base_url", "is_deleted", "kind", "last_modified_on", "model", "name", "protected_api_key", "tenant_id", "version")
                SELECT "id", COALESCE("activated_on", ''), "base_url", "is_deleted", "kind", "last_modified_on", "model", "name", "protected_api_key", "tenant_id", "version"
                FROM "model_providers";
                DROP TABLE "model_providers";
                ALTER TABLE "ef_temp_model_providers" RENAME TO "model_providers";
                CREATE INDEX "ix_model_providers_tenant_id_activated_on" ON "model_providers" ("tenant_id", "activated_on");
                """);
        }
    }
}
