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

    private static async Task WriteAsync(FileKey key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        await Store.WriteAsync(key, content, CancellationToken.None);
    }

    private static async Task<(Tenant Tenant, User Admin)> AddTenantWithAdminAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Role = UserRole.Admin().Value);
        await InsertAsync(tenant);
        await InsertAsync(admin);
        return (tenant, admin);
    }
}
