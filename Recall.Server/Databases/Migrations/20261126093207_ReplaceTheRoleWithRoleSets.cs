using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <summary>
    /// A user had one role, and the role gave its permissions as rows once. Now a user has a set of roles, the code
    /// gives the permissions of each role, and the rows of <c>user_permissions</c> are overrides: a Grant adds a
    /// permission that no role gives, and a Deny removes one that a role gives. The data steps keep the permissions that
    /// each user holds. The role <c>User</c> gave nothing, so it becomes no role. The names are literals, because a
    /// migration is fixed in time.
    /// </summary>
    /// <remarks>
    /// The role column drops in place with <c>ALTER TABLE ... DROP COLUMN</c>. The EF rebuild of <c>users</c> would drop
    /// the trigger <c>users_purge_journal</c>.
    /// </remarks>
    public partial class ReplaceTheRoleWithRoleSets : Migration
    {
        private const string AllPermissions = """
            VALUES ('manage_users'), ('manage_user_access'), ('manage_tenant'), ('manage_agents'), ('read_audit_log'),
                   ('read_audit_log_sensitive'), ('read_users'), ('read_agents'), ('manage_model_providers')
            """;

        // The permissions of each role as of this migration, for Down.
        private const string RolePermissions = """
            SELECT 'Admin' AS role, column1 AS permission FROM (VALUES ('manage_users'), ('manage_user_access'),
                ('manage_tenant'), ('manage_agents'), ('read_audit_log'), ('read_audit_log_sensitive'), ('read_users'),
                ('read_agents'), ('manage_model_providers'))
            UNION SELECT 'UserManager', column1 FROM (VALUES ('manage_users'), ('manage_user_access'), ('read_users'))
            UNION SELECT 'Auditor', column1 FROM (VALUES ('read_audit_log'), ('read_audit_log_sensitive'), ('read_users'), ('read_agents'))
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each row that exists is a permission that its user holds, so it starts as a Grant.
            migrationBuilder.AddColumn<string>(
                name: "effect",
                table: "user_permissions",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "Grant");

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "BINARY"),
                    role = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    last_modified_on = table.Column<string>(type: "TEXT", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_id",
                table: "user_roles",
                column: "user_id");

            migrationBuilder.Sql($"""
                INSERT INTO user_roles (id, user_id, role, is_deleted, version)
                SELECT {PermissionMigrationExtensions.NewIdSql("urole")}, u.id, 'Admin', 0, 1
                FROM users u
                WHERE u.role = 'Admin' AND NOT u.is_deleted;
                """);

            // An Admin who lacked a permission keeps lacking it, as a Deny.
            migrationBuilder.Sql($"""
                INSERT INTO user_permissions (id, user_id, permission, effect, is_deleted, version)
                SELECT {PermissionMigrationExtensions.NewIdSql("uperm")}, u.id, p.column1, 'Deny', 0, 1
                FROM users u CROSS JOIN ({AllPermissions}) p
                WHERE u.role = 'Admin' AND NOT u.is_deleted
                  AND NOT EXISTS (
                      SELECT 1 FROM user_permissions h
                      WHERE h.user_id = u.id AND h.permission = p.column1 AND NOT h.is_deleted AND h.effect = 'Grant');
                """);

            // The role Admin gives each permission, so a Grant of an Admin says nothing.
            migrationBuilder.Sql("""
                DELETE FROM user_permissions
                WHERE effect = 'Grant' AND user_id IN (SELECT id FROM users WHERE role = 'Admin');
                """);

            migrationBuilder.Sql("ALTER TABLE users DROP COLUMN role;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'User';");
            migrationBuilder.Sql("""
                UPDATE users SET role = 'Admin'
                WHERE id IN (SELECT user_id FROM user_roles WHERE role = 'Admin' AND NOT is_deleted);
                """);

            // Each permission that a role gives and no Deny removes becomes a row again.
            migrationBuilder.Sql($"""
                INSERT INTO user_permissions (id, user_id, permission, effect, is_deleted, version)
                SELECT {PermissionMigrationExtensions.NewIdSql("uperm")}, given.user_id, given.permission, 'Grant', 0, 1
                FROM (
                    SELECT DISTINCT r.user_id, rp.permission
                    FROM user_roles r JOIN ({RolePermissions}) rp ON rp.role = r.role
                    WHERE NOT r.is_deleted) given
                WHERE NOT EXISTS (
                    SELECT 1 FROM user_permissions h
                    WHERE h.user_id = given.user_id AND h.permission = given.permission AND NOT h.is_deleted);
                """);
            migrationBuilder.Sql("DELETE FROM user_permissions WHERE effect = 'Deny';");

            migrationBuilder.DropTable(name: "user_roles");
            migrationBuilder.Sql("ALTER TABLE user_permissions DROP COLUMN effect;");
        }
    }
}
