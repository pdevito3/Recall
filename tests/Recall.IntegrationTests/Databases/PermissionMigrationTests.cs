namespace Recall.IntegrationTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;

public sealed class PermissionMigrationTests : IAsyncDisposable
{
    // The last migration with the column users.role, before a user had a set of roles.
    private const string BeforeRoleSets = "20261123063809_ListEachMatchOfAPeopleProposal";
    private const string RoleSets = "20261126093207_ReplaceTheRoleWithRoleSets";

    private readonly SqliteTestDatabase _database = new();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Test]
    public async Task grant_before_the_role_sets_gives_the_permission_to_each_live_user_with_the_role_once()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(BeforeRoleSets);
        await ExecuteAsync(ArrangeWithRoleSql);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.GrantPermissionToRole("Admin", "manage_agents");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
        {
            await ExecuteAsync(sql);
            await ExecuteAsync(sql);
        }

        // Assert
        var granted = await UsersWithAsync("manage_agents");
        granted.ShouldBe(["usr_admin"]);
    }

    [Test]
    public async Task grant_to_holders_gives_the_permission_to_each_live_holder_once_and_a_new_id()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(ArrangeSql);
        await ExecuteAsync("""
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version) VALUES
                ('uperm_held', 'usr_member', 'read_users', false, 1),
                ('uperm_soft_deleted', 'usr_admin', 'read_users', true, 1);
            INSERT INTO user_permissions (id, user_id, permission, effect, is_deleted, version) VALUES
                ('uperm_denied', 'usr_admin', 'read_users', 'Deny', false, 1);
            """);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.GrantPermissionToHoldersOf("read_agents", "read_users");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
        {
            await ExecuteAsync(sql);
            await ExecuteAsync(sql);
        }

        // Assert
        (await UsersWithAsync("read_agents")).ShouldBe(["usr_member"]);
        var id = (await ScalarAsync("SELECT id FROM user_permissions WHERE permission = 'read_agents'")).ShouldBeOfType<string>();
        EntityId.IsValid(id, "uperm").ShouldBeTrue(id);
        Guid.Parse(id["uperm_".Length..]).Version.ShouldBe(7);
        Guid.Parse(id["uperm_".Length..]).Variant.ShouldBeInRange(8, 11);
    }

    [Test]
    public async Task delete_permission_removes_each_row_of_the_permission_also_a_deny_and_a_soft_deleted_row()
    {
        // Arrange
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();
        await ExecuteAsync(ArrangeSql);
        await ExecuteAsync("""
            INSERT INTO user_permissions (id, user_id, permission, effect, is_deleted, version) VALUES
                ('uperm_gone', 'usr_member', 'old_permission', 'Grant', false, 1),
                ('uperm_gone_too', 'usr_admin', 'old_permission', 'Grant', true, 1),
                ('uperm_denied', 'usr_admin', 'old_permission', 'Deny', false, 1),
                ('uperm_kept', 'usr_admin', 'read_users', 'Deny', false, 1);
            """);
        var builder = new MigrationBuilder(context.Database.ProviderName);
        builder.DeletePermission("old_permission");

        // Act
        foreach (var sql in builder.Operations.Cast<SqlOperation>().Select(o => o.Sql))
            await ExecuteAsync(sql);

        // Assert
        (await ScalarAsync("SELECT count(*) FROM user_permissions WHERE permission = 'old_permission'")).ShouldBe(0L);
        (await ScalarAsync("SELECT count(*) FROM user_permissions WHERE permission = 'read_users'")).ShouldBe(1L);
    }

    [Test]
    public async Task an_admin_of_the_first_migration_is_a_full_admin_after_the_last_migration()
    {
        // Arrange
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261105074122_InitialSqlite");
        await ExecuteAsync("""
            INSERT INTO tenants (id, name, is_deleted, version) VALUES ('ten_first', 'First tenant', false, 1);
            INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, role, is_deleted, version)
            VALUES ('usr_owner', 'ten_first', 'Olga', 'Owner', 'idp-owner', 'olga', 'olga@example.com', 'Admin', false, 1);
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version) VALUES
                ('uperm_1', 'usr_owner', 'manage_users', false, 1),
                ('uperm_2', 'usr_owner', 'manage_user_access', false, 1),
                ('uperm_3', 'usr_owner', 'manage_tenant', false, 1),
                ('uperm_4', 'usr_owner', 'manage_agents', false, 1),
                ('uperm_5', 'usr_owner', 'read_audit_log', false, 1),
                ('uperm_6', 'usr_owner', 'read_audit_log_sensitive', false, 1),
                ('uperm_7', 'usr_owner', 'read_users', false, 1),
                ('uperm_8', 'usr_owner', 'read_agents', false, 1);
            """);

        // Act
        await migrator.MigrateAsync();

        // Assert
        (await RolesOfAsync("usr_owner")).ShouldBe([UserRole.AdminName]);
        (await OverridesOfAsync("usr_owner")).ShouldBeEmpty();
    }

    [Test]
    public async Task role_sets_keep_the_permissions_that_each_user_held()
    {
        // Arrange
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeRoleSets);
        await ExecuteAsync(ArrangeWithRoleSql);
        await ExecuteAsync($"""
            INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, role, is_deleted, version) VALUES
                ('usr_partadmin', 'ten_migration', 'Pia', 'Part', 'idp-partadmin', 'pia', 'pia@example.com', 'Admin', false, 1),
                ('usr_extra', 'ten_migration', 'Eva', 'Extra', 'idp-extra', 'eva', 'eva@example.com', 'User', false, 1);
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version)
                SELECT 'uperm_a_' || column1, 'usr_admin', column1, false, 1 FROM ({AllPermissionsSql});
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version)
                SELECT 'uperm_p_' || column1, 'usr_partadmin', column1, false, 1 FROM ({AllPermissionsSql})
                WHERE column1 <> 'read_audit_log_sensitive';
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version) VALUES
                ('uperm_x', 'usr_extra', 'read_users', false, 1);
            """);

        // Act
        await migrator.MigrateAsync(RoleSets);

        // Assert
        (await RolesOfAsync("usr_admin")).ShouldBe([UserRole.AdminName]);
        (await OverridesOfAsync("usr_admin")).ShouldBeEmpty();
        (await RolesOfAsync("usr_partadmin")).ShouldBe([UserRole.AdminName]);
        (await OverridesOfAsync("usr_partadmin")).ShouldBe(["read_audit_log_sensitive:Deny"]);
        (await RolesOfAsync("usr_extra")).ShouldBeEmpty();
        (await OverridesOfAsync("usr_extra")).ShouldBe(["read_users:Grant"]);
        (await RolesOfAsync("usr_member")).ShouldBeEmpty();
        (await OverridesOfAsync("usr_member")).ShouldBeEmpty();
        (await ScalarAsync("SELECT count(*) FROM pragma_table_info('users') WHERE name = 'role'")).ShouldBe(0L);
        (await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'users_purge_journal'"))
            .ShouldBe(1L);
    }

    [Test]
    public async Task down_from_role_sets_gives_back_the_role_and_the_rows_of_the_permissions_held()
    {
        // Arrange
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(RoleSets);
        await ExecuteAsync(ArrangeSql);
        await ExecuteAsync("""
            INSERT INTO user_roles (id, user_id, role, is_deleted, version) VALUES
                ('urole_a', 'usr_admin', 'Admin', false, 1),
                ('urole_m1', 'usr_member', 'UserManager', false, 1),
                ('urole_m2', 'usr_member', 'Auditor', false, 1);
            INSERT INTO user_permissions (id, user_id, permission, effect, is_deleted, version) VALUES
                ('uperm_d', 'usr_admin', 'manage_tenant', 'Deny', false, 1);
            """);

        // Act
        await migrator.MigrateAsync(BeforeRoleSets);

        // Assert
        (await StringsAsync("SELECT role FROM users WHERE id = @value", "usr_admin")).ShouldBe(["Admin"]);
        (await StringsAsync("SELECT role FROM users WHERE id = @value", "usr_member")).ShouldBe(["User"]);
        (await PermissionsOfAsync("usr_admin")).ShouldBe(
            ["manage_users", "manage_user_access", "manage_agents", "read_audit_log", "read_audit_log_sensitive",
             "read_users", "read_agents", "manage_model_providers"], ignoreOrder: true);
        (await PermissionsOfAsync("usr_member")).ShouldBe(
            ["manage_users", "manage_user_access", "read_users", "read_audit_log", "read_audit_log_sensitive", "read_agents"],
            ignoreOrder: true);
        (await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'users_purge_journal'"))
            .ShouldBe(1L);
    }

    // The permissions as of the migration ReplaceTheRoleWithRoleSets, as literals, because a migration is fixed in time.
    private const string AllPermissionsSql = """
        VALUES ('manage_users'), ('manage_user_access'), ('manage_tenant'), ('manage_agents'), ('read_audit_log'),
               ('read_audit_log_sensitive'), ('read_users'), ('read_agents'), ('manage_model_providers')
        """;

    // Users of the schema since ReplaceTheRoleWithRoleSets, which has no column users.role.
    private const string ArrangeSql = """
        INSERT INTO tenants (id, name, is_deleted, version) VALUES ('ten_migration', 'Migration tenant', false, 1);
        INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, is_deleted, version) VALUES
            ('usr_admin', 'ten_migration', 'Ada', 'Admin', 'idp-admin', 'ada', 'ada@example.com', false, 1),
            ('usr_member', 'ten_migration', 'Mia', 'Member', 'idp-member', 'mia', 'mia@example.com', false, 1),
            ('usr_deletedadmin', 'ten_migration', 'Dan', 'Deleted', 'idp-deletedadmin', 'dan', 'dan@example.com', true, 1);
        """;

    private const string ArrangeWithRoleSql = """
        INSERT INTO tenants (id, name, is_deleted, version) VALUES ('ten_migration', 'Migration tenant', false, 1);
        INSERT INTO users (id, tenant_id, first_name, last_name, identifier, username, email, role, is_deleted, version) VALUES
            ('usr_admin', 'ten_migration', 'Ada', 'Admin', 'idp-admin', 'ada', 'ada@example.com', 'Admin', false, 1),
            ('usr_member', 'ten_migration', 'Mia', 'Member', 'idp-member', 'mia', 'mia@example.com', 'User', false, 1),
            ('usr_deletedadmin', 'ten_migration', 'Dan', 'Deleted', 'idp-deletedadmin', 'dan', 'dan@example.com', 'Admin', true, 1);
        """;

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, TimeProvider.System, Substitute.For<ICurrentUserService>(),
            Substitute.For<ICallerIdentityResolver>(), Substitute.For<ITenantScopeProvider>(), new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private Task<List<string>> UsersWithAsync(string permission)
        => StringsAsync("SELECT user_id FROM user_permissions WHERE permission = @value AND NOT is_deleted", permission);

    private Task<List<string>> PermissionsOfAsync(string userId)
        => StringsAsync("SELECT permission FROM user_permissions WHERE user_id = @value AND NOT is_deleted", userId);

    private Task<List<string>> RolesOfAsync(string userId)
        => StringsAsync("SELECT role FROM user_roles WHERE user_id = @value AND NOT is_deleted", userId);

    private Task<List<string>> OverridesOfAsync(string userId)
        => StringsAsync("SELECT permission || ':' || effect FROM user_permissions WHERE user_id = @value AND NOT is_deleted", userId);

    private async Task<List<string>> StringsAsync(string sql, string value)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}
