namespace Recall.IntegrationTests.Jobs;

using System.Diagnostics;
using BackWave.Monitor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Jobs;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class SaveAndEnqueueTests : TestBase
{
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
        var tenant = NewTenant();
        db.Tenants.Add(tenant);
        var jobId = Guid.Empty;

        // Act
        var act = () => db.SaveAndEnqueueAsync(async (jobs, ct) =>
        {
            jobId = await jobs.EnqueueAsync(new SystemHealth(), DateTimeOffset.UtcNow.AddDays(1), ct);
            throw new InvalidOperationException("The work after the enqueue fails.");
        });

        // Assert
        await act.ShouldThrowAsync<InvalidOperationException>();
        jobId.ShouldNotBe(Guid.Empty);
        (await TenantExistsAsync(tenant.Id)).ShouldBeFalse();
        (await JobAsync(jobId)).ShouldBeNull();
    }

    [Test]
    public async Task the_first_save_with_a_job_on_a_new_store_does_not_wait_for_its_own_write_lock()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        await using var provider = TestFixture.BuildServiceProvider(_ => { }, database.ConnectionString);
        await using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var tenant = NewTenant();
        db.Tenants.Add(tenant);
        var jobId = Guid.Empty;
        var clock = Stopwatch.StartNew();

        // Act
        await db.SaveAndEnqueueAsync(async (jobs, ct) =>
            jobId = await jobs.EnqueueAsync(new SystemHealth(), DateTimeOffset.UtcNow.AddDays(1), ct));

        // Assert
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        var monitor = scope.ServiceProvider.GetRequiredService<BackWaveMonitor>();
        (await monitor.GetJobAsync(jobId)).ShouldNotBeNull();
    }

    [Test]
    public async Task a_commit_keeps_the_row_and_the_job()
    {
        // Arrange
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = NewTenant();
        db.Tenants.Add(tenant);
        var jobId = Guid.Empty;

        // Act
        await db.SaveAndEnqueueAsync(async (jobs, ct) =>
            jobId = await jobs.EnqueueAsync(new SystemHealth(), DateTimeOffset.UtcNow.AddDays(1), ct));

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
    public async Task a_lookup_during_the_enqueue_does_not_keep_a_deleted_user_live()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var user)
            .BuildAsync();
        var provider = testingServiceScope.GetService<ITenantIdProvider>();
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Users.FirstAsync(u => u.Id == user.Id);
        db.Users.Remove(tracked.Delete());
        LiveUser? duringTheEnqueue = null;

        // Act
        await db.SaveAndEnqueueAsync(async (_, ct) => duringTheEnqueue = await provider.FindLiveUserAsync(user.Identifier, ct));
        var afterTheCommit = await provider.FindLiveUserAsync(user.Identifier);

        // Assert
        duringTheEnqueue.ShouldBe(new LiveUser(user.Id, user.TenantId));
        afterTheCommit.ShouldBeNull();
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
