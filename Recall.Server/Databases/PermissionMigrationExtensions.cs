namespace Recall.Server.Databases;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Data steps for the rows of <c>user_permissions</c> and <c>agent_permissions</c>. A migration is fixed in time, so
/// pass the role and the permission as string literals, not as <c>UserRole</c> or <c>Permission</c> factories.
/// </summary>
public static class PermissionMigrationExtensions
{
    private static readonly PermissionTable UserPermissions = new("user_permissions", "uperm", "users", "user_id");
    private static readonly PermissionTable AgentPermissions = new("agent_permissions", "aperm", "agents", "agent_id");

    /// <summary>
    /// Gives the permission to each live user with the role who has no live row for it. A second run changes nothing.
    /// </summary>
    public static void GrantPermissionToRole(this MigrationBuilder migrationBuilder, string role, string permission)
        => migrationBuilder.GrantPermission(UserPermissions, permission, $"o.role = {Literal(role)}");

    /// <summary>
    /// Gives the permission to each live user and to each live agent that has a live row for
    /// <paramref name="heldPermission"/> or one of <paramref name="otherHeldPermissions"/>, and no live row for the
    /// permission. For an agent, the rows are its grant. A second run changes nothing.
    /// </summary>
    public static void GrantPermissionToHoldersOf(this MigrationBuilder migrationBuilder, string permission,
        string heldPermission, params string[] otherHeldPermissions)
    {
        var held = string.Join(", ", otherHeldPermissions.Prepend(heldPermission).Select(Literal));
        foreach (var table in new[] { UserPermissions, AgentPermissions })
        {
            migrationBuilder.GrantPermission(table, permission, $"""
                EXISTS (
                      SELECT 1 FROM {table.Name} h
                      WHERE h.{table.OwnerColumn} = o.id AND NOT h.is_deleted AND h.permission IN ({held}))
                """);
        }
    }

    public static void DeletePermission(this MigrationBuilder migrationBuilder, string permission)
        => migrationBuilder.Sql($"""
            DELETE FROM user_permissions WHERE permission = {Literal(permission)};
            DELETE FROM agent_permissions WHERE permission = {Literal(permission)};
            """);

    /// <summary>
    /// Gives the permission to each live owner that matches <paramref name="ownerCondition"/>, a SQL condition on the
    /// alias <c>o</c> of the owner table, and has no live row for the permission.
    /// </summary>
    private static void GrantPermission(
        this MigrationBuilder migrationBuilder, PermissionTable table, string permission, string ownerCondition)
    {
        // The same ID as EntityId.New: the prefix and a UUIDv7 in hex. It has the Unix time in milliseconds in the first
        // 48 bits, the version nibble 7, the variant bits, and random bits.
        var newId = $"""
            '{table.IdPrefix}_'
                || printf('%012x', CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
                || '7' || lower(substr(hex(randomblob(2)), 2))
                || substr('89ab', 1 + (abs(random()) % 4), 1) || lower(substr(hex(randomblob(8)), 2))
            """;

        migrationBuilder.Sql($"""
            INSERT INTO {table.Name} (id, {table.OwnerColumn}, permission, is_deleted, version)
            SELECT {newId}, o.id, {Literal(permission)}, 0, 1
            FROM {table.Owners} o
            WHERE {ownerCondition}
              AND NOT o.is_deleted
              AND NOT EXISTS (
                  SELECT 1 FROM {table.Name} p
                  WHERE p.{table.OwnerColumn} = o.id AND p.permission = {Literal(permission)} AND NOT p.is_deleted);
            """);
    }

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <param name="Name">The table of the permission rows.</param>
    /// <param name="IdPrefix">The ID prefix of a row.</param>
    /// <param name="Owners">The table of the users or the agents that own the rows.</param>
    /// <param name="OwnerColumn">The column of a row that holds the ID of its owner.</param>
    private sealed record PermissionTable(string Name, string IdPrefix, string Owners, string OwnerColumn);
}
