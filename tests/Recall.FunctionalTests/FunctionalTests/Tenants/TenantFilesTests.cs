namespace Recall.FunctionalTests.FunctionalTests.Tenants;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using TestUtilities;

public class TenantFilesTests : TestBase
{
    private static FileStore Store => TestingWebApplicationFactory.Instance.Services.GetRequiredService<FileStore>();

    [Test]
    public async Task a_tenant_delete_removes_the_files_of_that_tenant_and_no_others()
    {
        // Arrange
        var (tenant, admin) = await AddTenantWithAdminAsync();
        var (otherTenant, _) = await AddTenantWithAdminAsync();
        var files = FileKey.ForTenant(tenant.Id, "recordings", "rec_1", "00001");
        var otherFiles = FileKey.ForTenant(otherTenant.Id, "recordings", "rec_1", "00001");
        await WriteAsync(files, [1]);
        await WriteAsync(otherFiles, [2]);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Should.Throw<NotFoundException>(() => Store.OpenRead(files));
        await using var kept = Store.OpenRead(otherFiles);
        kept.ReadByte().ShouldBe(2);
    }

    [Test]
    public async Task a_tenant_delete_that_cannot_delete_the_files_keeps_them_and_still_deletes_the_tenant()
    {
        // Arrange
        var (tenant, admin) = await AddTenantWithAdminAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"recall_test_outside_{Guid.NewGuid():N}");
        var outsideFile = Path.Combine(outside, "file");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(outsideFile, [1]);
        var tenantsFolder = Path.Combine(TestingWebApplicationFactory.Instance.FileRoot, "tenants");
        Directory.CreateDirectory(tenantsFolder);
        var link = Path.Combine(tenantsFolder, tenant.Id);
        Directory.CreateSymbolicLink(link, outside);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        try
        {
            // Act
            var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id));

            // Assert
            result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await ExecuteDbContextAsync(db => db.Tenants.AnyAsync(t => t.Id == tenant.Id))).ShouldBeFalse();
            (await File.ReadAllBytesAsync(outsideFile)).ShouldBe([1]);
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task a_delete_that_a_member_may_not_make_keeps_the_files()
    {
        // Arrange
        var (tenant, member) = await AddTenantWithUserAsync(UserRole.User());
        var files = FileKey.ForTenant(tenant.Id, "recordings", "rec_1", "00001");
        await WriteAsync(files, [1]);
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var kept = Store.OpenRead(files);
        kept.ReadByte().ShouldBe(1);
    }

    [Test]
    public async Task a_delete_of_an_unknown_tenant_keeps_the_files_under_that_id()
    {
        // Arrange
        var (_, admin) = await AddTenantWithAdminAsync();
        var unknownTenantId = $"ten_{Guid.NewGuid():N}";
        var files = FileKey.ForTenant(unknownTenantId, "recordings", "rec_1", "00001");
        await WriteAsync(files, [1]);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(unknownTenantId));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var kept = Store.OpenRead(files);
        kept.ReadByte().ShouldBe(1);
    }

    [Test]
    public async Task a_tenant_delete_whose_commit_fails_keeps_the_files_because_the_rows_commit_first()
    {
        // Arrange
        var (tenant, admin) = await AddTenantWithAdminAsync();
        var files = FileKey.ForTenant(tenant.Id, "recordings", "rec_1", "00001");
        await WriteAsync(files, [1]);
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var names = Guid.NewGuid().ToString("N");
        // A deferred foreign key fails at the commit itself, after the save has sent each statement of the delete.
        var failTheCommit = $"""
            CREATE TABLE parent_{names} (id TEXT PRIMARY KEY);
            CREATE TABLE child_{names} (parent_id TEXT REFERENCES parent_{names} (id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fail_commit_{names} AFTER UPDATE ON tenants WHEN NEW.id = '{tenant.Id}'
            BEGIN INSERT INTO child_{names} VALUES ('missing'); END;
            """;
        var cleanUp = $"""
            DROP TRIGGER fail_commit_{names};
            DROP TABLE child_{names};
            DROP TABLE parent_{names};
            """;
        await ExecuteSqlPastTheWriteGuardAsync(failTheCommit);

        try
        {
            // Act
            var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Tenants.Delete(tenant.Id));

            // Assert
            result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            (await ExecuteDbContextAsync(db => db.Tenants.AnyAsync(t => t.Id == tenant.Id))).ShouldBeTrue();
            await using var kept = Store.OpenRead(files);
            kept.ReadByte().ShouldBe(1);
        }
        finally
        {
            await ExecuteSqlPastTheWriteGuardAsync(cleanUp);
        }
    }

    private static Task<int> ExecuteSqlPastTheWriteGuardAsync(string sql) => ExecuteDbContextAsync(async db =>
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    });

    private static async Task WriteAsync(FileKey key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        await Store.WriteAsync(key, content, CancellationToken.None);
    }

    private static Task<(Tenant Tenant, User User)> AddTenantWithAdminAsync() => AddTenantWithUserAsync(UserRole.Admin());

    private static async Task<(Tenant Tenant, User User)> AddTenantWithUserAsync(UserRole role)
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var user, u => u.Role = role.Value);
        await InsertAsync(tenant);
        await InsertAsync(user);
        return (tenant, user);
    }
}
