namespace Recall.IntegrationTests.Domain.ModelProviders;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Recall.Server.Databases;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Exceptions;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

/// <summary>A key ring is a folder. A restore with no such folder, or a folder that takes no write, makes a new ring.</summary>
public class KeyRingWatchTests
{
    private static IDataProtectionProvider NewRing() => DataProtectionProvider.Create(Directory.CreateTempSubdirectory("recall-ring"));

    private static async Task<ServiceProvider> DatabaseAsync(SqliteTestDatabase database)
    {
        var services = TestFixture.BuildServiceProvider(_ => { }, database.ConnectionString);
        await using var system = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        return services;
    }

    private static IConfiguration ConfigurationOver(DirectoryInfo dataFolder) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DatabaseConsts.DatabaseName}"] = $"Data Source={Path.Combine(dataFolder.FullName, "recall.db")}",
        })
        .Build();

    private static async Task<KeyRingStatus> StartWatchAsync(IServiceProvider services, DirectoryInfo dataFolder, IDataProtectionProvider ring)
    {
        var status = new KeyRingStatus();
        var watch = new KeyRingWatch(services.GetRequiredService<IServiceScopeFactory>(), ring, new ApiKeyProtector(ring), status, ConfigurationOver(dataFolder));
        await watch.StartAsync(CancellationToken.None);
        return status;
    }

    private static async Task ConnectProviderUnderAsync(IServiceProvider services, IDataProtectionProvider ring)
    {
        TestGraph.Start(out _).AddTenant(out var tenant);
        var protectedKey = new ApiKeyProtector(ring).Protect($"fake-key-{Guid.NewGuid():N}");
        await using var system = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Tenants.Add(tenant);
        dbContext.ModelProviders.Add(ModelProvider.Connect(tenant.Id, ModelProviderKind.Anthropic, "Mine", null, protectedKey, DateTimeOffset.UnixEpoch));
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task the_first_start_marks_the_ring_and_the_next_start_with_the_same_ring_warns_of_nothing()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var services = await DatabaseAsync(database);
        var ring = NewRing();
        var folder = Directory.CreateTempSubdirectory("recall-data");

        // Act
        var first = await StartWatchAsync(services, folder, ring);
        var second = await StartWatchAsync(services, folder, ring);

        // Assert
        first.Warning.ShouldBeNull();
        second.Warning.ShouldBeNull();
    }

    [Test]
    public async Task a_restore_with_another_ring_warns_and_the_warning_stays_while_a_key_is_unreadable()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var services = await DatabaseAsync(database);
        var oldRing = NewRing();
        var folder = Directory.CreateTempSubdirectory("recall-data");
        await StartWatchAsync(services, folder, oldRing);
        await ConnectProviderUnderAsync(services, oldRing);

        // Act
        var newRing = NewRing();
        var afterRestore = await StartWatchAsync(services, folder, newRing);
        var afterNextStart = await StartWatchAsync(services, folder, newRing);

        // Assert
        afterRestore.Warning.ShouldBe(KeyRingStatus.ChangedText);
        afterNextStart.Warning.ShouldBe(KeyRingStatus.ChangedText);
    }

    [Test]
    public async Task a_restore_with_another_ring_and_no_saved_key_warns_at_one_start()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var services = await DatabaseAsync(database);
        var folder = Directory.CreateTempSubdirectory("recall-data");
        await StartWatchAsync(services, folder, NewRing());
        var newRing = NewRing();

        // Act
        var afterRestore = await StartWatchAsync(services, folder, newRing);
        var afterNextStart = await StartWatchAsync(services, folder, newRing);

        // Assert
        afterRestore.Warning.ShouldBe(KeyRingStatus.ChangedText);
        afterNextStart.Warning.ShouldBeNull();
    }

    [Test]
    public async Task keys_entered_again_after_a_restore_clear_the_warning_at_the_next_start()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var services = await DatabaseAsync(database);
        var folder = Directory.CreateTempSubdirectory("recall-data");
        await StartWatchAsync(services, folder, NewRing());
        var newRing = NewRing();
        await ConnectProviderUnderAsync(services, newRing);

        // Act
        var nextStart = await StartWatchAsync(services, folder, newRing);
        var startAfter = await StartWatchAsync(services, folder, newRing);

        // Assert
        nextStart.Warning.ShouldBeNull();
        startAfter.Warning.ShouldBeNull();
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task a_key_folder_that_takes_no_write_warns()
    {
        // Arrange
        await using var database = new SqliteTestDatabase();
        var services = await DatabaseAsync(database);
        var folder = Directory.CreateTempSubdirectory("recall-data");
        var keys = folder.CreateSubdirectory("keys");
        File.SetUnixFileMode(keys.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        KeyRingStatus status;
        var ring = DataProtectionProvider.Create(keys);
        Action save = () => new ApiKeyProtector(ring).Protect("a-key");

        // Act: the ring of the server lives in this folder, so it holds no key and cannot make one.
        try
        {
            status = await StartWatchAsync(services, folder, ring);
            save.ShouldThrow<ValidationException>();
        }
        finally
        {
            File.SetUnixFileMode(keys.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Assert
        status.FolderIsWritable.ShouldBeFalse();
        status.Warning.ShouldBe(KeyRingStatus.NotWritableText);
    }

    [Test]
    public async Task the_health_check_is_degraded_while_there_is_a_warning()
    {
        // Arrange
        var status = new KeyRingStatus { RingChanged = true };
        var check = new KeyRingHealthCheck(status);

        // Act
        var warned = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        status.RingChanged = false;
        var calm = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        warned.Status.ShouldBe(HealthStatus.Degraded);
        warned.Description.ShouldBe(KeyRingStatus.ChangedText);
        calm.Status.ShouldBe(HealthStatus.Healthy);
    }
}
