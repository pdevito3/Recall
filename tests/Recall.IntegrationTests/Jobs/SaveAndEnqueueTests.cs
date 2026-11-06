namespace Recall.IntegrationTests.Jobs;

using BackWave;
using BackWave.Monitor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Resources.Jobs;
using Shouldly;

public class SaveAndEnqueueTests : TestBase
{
    // A host migrates the job tables when it starts, and no worker runs here. The first use of the store migrates them,
    // and that needs the write lock, so it cannot run inside a transaction of this test.
    [Before(Test)]
    public async Task MigrateTheJobTablesAsync()
    {
        await using var scope = TestFixture.BaseScopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new SystemHealth(), dueTime: DateTimeOffset.UtcNow.AddDays(1));
    }

    private static Tenant NewTenant() => Tenant.Create(new TenantForCreation { Name = $"jobs-{Guid.NewGuid():N}" });

    private static async Task<bool> TenantExistsAsync(string tenantId)
    {
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants.AnyAsync(t => t.Id == tenantId);
    }

    private static async Task<JobSnapshot?> JobAsync(Guid jobId)
    {
        await using var scope = TestFixture.BaseScopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackWaveMonitor>().GetJobAsync(jobId);
    }

    [Test]
    public async Task a_throw_after_the_enqueue_leaves_neither_the_row_nor_the_job()
    {
        // Arrange
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<BackWaveClient>();
        var tenant = NewTenant();
        db.Tenants.Add(tenant);
        var jobId = Guid.Empty;

        // Act
        var act = () => db.SaveAndEnqueueAsync(async (transaction, ct) =>
        {
            jobId = await client.EnqueueAsync(new SystemHealth(), dueTime: DateTimeOffset.UtcNow.AddDays(1), transaction: transaction, cancellationToken: ct);
            throw new InvalidOperationException("The work after the enqueue fails.");
        });

        // Assert
        await act.ShouldThrowAsync<InvalidOperationException>();
        jobId.ShouldNotBe(Guid.Empty);
        (await TenantExistsAsync(tenant.Id)).ShouldBeFalse();
        (await JobAsync(jobId)).ShouldBeNull();
    }

    [Test]
    public async Task a_commit_keeps_the_row_and_the_job()
    {
        // Arrange
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<BackWaveClient>();
        var tenant = NewTenant();
        db.Tenants.Add(tenant);
        var jobId = Guid.Empty;

        // Act
        await db.SaveAndEnqueueAsync(async (transaction, ct) =>
            jobId = await client.EnqueueAsync(new SystemHealth(), dueTime: DateTimeOffset.UtcNow.AddDays(1), transaction: transaction, cancellationToken: ct));

        // Assert
        (await TenantExistsAsync(tenant.Id)).ShouldBeTrue();
        (await JobAsync(jobId)).ShouldNotBeNull();
    }

    [Test]
    public async Task a_failed_save_with_a_job_ends_the_context()
    {
        // Arrange
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(NewTenant());
        var failed = () => db.SaveAndEnqueueAsync((_, _) => throw new InvalidOperationException("The enqueue fails."));
        await failed.ShouldThrowAsync<InvalidOperationException>();

        // Act
        var later = () => db.SaveChangesAsync();

        // Assert
        (await later.ShouldThrowAsync<InvalidOperationException>()).Message.ShouldContain("Use a new DI scope");
    }

    [Test]
    public async Task a_save_with_a_job_is_refused_inside_a_transaction_that_the_caller_opened()
    {
        // Arrange
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(NewTenant());
        await using var opened = await db.Database.BeginTransactionAsync();

        // Act
        var act = () => db.SaveAndEnqueueAsync((_, _) => Task.CompletedTask);

        // Assert
        await act.ShouldThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task the_health_job_sees_the_rows_of_each_tenant()
    {
        // Arrange
        var jobs = new SystemHealthJobs(TestFixture.BaseScopeFactory, Options.Create(new JobOptions()));

        // Act
        var tenantCount = await jobs.TenantCountAsync(CancellationToken.None);

        // Assert
        tenantCount.ShouldBeGreaterThan(0);
    }
}
