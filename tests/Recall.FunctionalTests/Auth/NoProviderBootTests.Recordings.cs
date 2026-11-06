namespace Recall.FunctionalTests.Auth;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Databases;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public partial class NoProviderBootTests
{
    private sealed class SeedHost(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(TestingConsts.FunctionalTestingEnvName);
            builder.UseSetting("Jobs:RunWorkers", "false");
            builder.UseSetting($"ConnectionStrings:{DatabaseConsts.DatabaseName}", $"Data Source={Path.Combine(root, "data", "recall.db")}");
            builder.UseSetting("FileStore:Root", Path.Combine(root, "data", "files"));
            builder.ConfigureServices(services => services.AddHostedService<MigrationHostedService<AppDbContext>>());
        }
    }

    private sealed record Seeded(string Cookie, string DeletedTenantId, string KeptTenantId);

    /// <summary>Makes the rows with the real store, so the server process reads a real session and no test code runs in it.</summary>
    private static async Task<Seeded> SeedTwoTenantsWithFilesAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        await using var host = new SeedHost(root);
        _ = host.Services;
        await using var scope = host.Services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        TestGraph.Start(out _).AddTenant(out var deleted).AddUser(out var admin, u => u.Role = UserRole.Admin().Value);
        TestGraph.Start(out _).AddTenant(out var kept);
        db.Add(deleted);
        db.Add(kept);
        db.Add(admin);
        await db.SaveChangesAsync();

        // A login makes the identifier of a person the ID of the row, and a session names that ID.
        await db.Database.OpenConnectionAsync();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "UPDATE users SET identifier = id";
            await command.ExecuteNonQueryAsync();
        }

        var session = await scope.ServiceProvider.GetRequiredService<SessionStore>().StartWebAsync(admin.Id, "stub", CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<FileStore>();
        foreach (var tenantId in new[] { deleted.Id, kept.Id })
        {
            using var content = new MemoryStream([1]);
            await store.WriteAsync(FileKey.ForTenant(tenantId, RecordingFiles.RootFolder, admin.Id, "rec_1", "audio.webm"), content, CancellationToken.None);
        }

        return new Seeded(session.CookieValue, deleted.Id, kept.Id);
    }

    [Test]
    [NotInParallel("recording-seed-host")]
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
    [NotInParallel("recording-seed-host")]
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
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        server.HasExited.ShouldBeFalse();
    }
}
