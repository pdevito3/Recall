namespace Recall.FunctionalTests.Auth;

using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public partial class NoProviderBootTests
{
    private sealed record Seeded(string Cookie, string DeletedTenantId, string KeptTenantId);

    private static async Task<Seeded> SeedTwoTenantsWithFilesAsync(string root)
    {
        var migrating = await ServerProcess.StartAsync(root);
        var database = migrating.Database;
        await migrating.KillAsync();

        var deleted = EntityId.New(Tenant.IdPrefix);
        var kept = EntityId.New(Tenant.IdPrefix);
        var admin = EntityId.New(User.IdPrefix);
        var cookie = WebSession.CookiePrefix + Secrets.New();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tenants (id, name, is_deleted, version) VALUES ($deleted, 'Deleted', 0, 1), ($kept, 'Kept', 0, 1);
                INSERT INTO users (id, tenant_id, first_name, identifier, username, role, is_deleted, version)
                VALUES ($admin, $deleted, 'Ada', $admin, 'ada', 'Admin', 0, 1);
                INSERT INTO user_permissions (id, user_id, permission, is_deleted, version)
                VALUES ($permission, $admin, 'manage_tenant', 0, 1);
                INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, last_slid_at)
                VALUES ($session, $admin, 'web', 'stub', $hash, $expires, $expires, $now, $now);
                """;
            command.Parameters.AddWithValue("$deleted", deleted);
            command.Parameters.AddWithValue("$kept", kept);
            command.Parameters.AddWithValue("$admin", admin);
            command.Parameters.AddWithValue("$session", EntityId.New(AuthSession.IdPrefix));
            command.Parameters.AddWithValue("$permission", EntityId.New("uperm"));
            command.Parameters.AddWithValue("$hash", TokenHash.Of(cookie).Hex);
            command.Parameters.AddWithValue("$expires", now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$now", now.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        var store = new FileStore(Options.Create(new FileStoreOptions { Root = Path.Combine(root, "data", "files") }));
        foreach (var tenantId in new[] { deleted, kept })
        {
            using var content = new MemoryStream([1]);
            await store.WriteAsync(FileKey.ForTenant(tenantId, RecordingFiles.RootFolder, admin, "rec_1", "audio.webm"), content, CancellationToken.None);
        }

        return new Seeded(cookie, deleted, kept);
    }

    [Test]
    [Property("Rule", "REC-11")]
    public async Task a_kill_between_the_commit_and_the_folder_delete_leaves_a_folder_that_the_next_start_removes()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
        var seeded = await SeedTwoTenantsWithFilesAsync(root);
        var deletedFolder = Path.Combine(root, "data", "files", "tenants", seeded.DeletedTenantId);
        var keptFolder = Path.Combine(root, "data", "files", "tenants", seeded.KeptTenantId);
        var testHost = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = TestingConsts.FunctionalTestingEnvName };
        var killing = new Dictionary<string, string>(testHost) { ["Jobs__KillBeforeTenantFolderDelete"] = "true" };

        // Act
        await using var first = await ServerProcess.StartAsync(root, killing);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tenants/{seeded.DeletedTenantId}");
        request.Headers.Add("Cookie", $"{SessionCookie.PlainName}={seeded.Cookie}");
        request.Headers.Add("X-CSRF", "1");
        await Should.ThrowAsync<HttpRequestException>(() => first.Client.SendAsync(request));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await first.WaitForExitAsync(limit.Token);
        first.ExitCode.ShouldNotBe(0);
        Directory.Exists(deletedFolder).ShouldBeTrue("The kill must come before the folder delete.");
        await first.KillAsync();
        (await IsSoftDeletedAsync(first.Database, seeded.DeletedTenantId)).ShouldBeTrue("The kill must come after the commit.");

        await using var second = await ServerProcess.StartAsync(root, testHost);
        for (var attempt = 0; attempt < 300 && Directory.Exists(deletedFolder); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Assert
        Directory.Exists(deletedFolder).ShouldBeFalse();
        Directory.Exists(keptFolder).ShouldBeTrue();
        second.HasExited.ShouldBeFalse();
    }

    private static async Task<bool> IsSoftDeletedAsync(string database, string tenantId)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT is_deleted FROM tenants WHERE id = $id", connection);
        command.Parameters.AddWithValue("$id", tenantId);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    [Test]
    public async Task a_production_host_ignores_the_kill_setting()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
        var seeded = await SeedTwoTenantsWithFilesAsync(root);
        var production = new Dictionary<string, string> { ["Jobs__KillBeforeTenantFolderDelete"] = "true" };

        // Act
        await using var server = await ServerProcess.StartAsync(root, production);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tenants/{seeded.DeletedTenantId}");
        request.Headers.Add("Cookie", $"{SessionCookie.PlainName}={seeded.Cookie}");
        request.Headers.Add("X-CSRF", "1");
        using var response = await server.Client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, server.Output);
        server.HasExited.ShouldBeFalse();
    }
}
