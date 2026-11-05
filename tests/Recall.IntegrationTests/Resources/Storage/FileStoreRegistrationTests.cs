namespace Recall.IntegrationTests.Resources.Storage;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Resources.Extensions;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class FileStoreRegistrationTests
{
    [Test]
    [Arguments("")]
    [Arguments("relative/files")]
    public async Task a_start_with_a_root_that_is_not_a_full_path_fails_and_names_the_variable(string root)
    {
        // Arrange
        await using var app = BuildWithRoot(root);

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => app.Services.GetRequiredService<IStartupValidator>().Validate());

        // Assert
        failure.Message.ShouldContain("FileStore__Root");
    }

    [Test]
    public async Task a_start_removes_what_an_interrupted_write_left()
    {
        // Arrange
        using var root = new TemporaryFileRoot();
        var temporaryFolder = Path.Combine(root.Path, FileKey.TemporaryFolder);
        Directory.CreateDirectory(temporaryFolder);
        await File.WriteAllBytesAsync(Path.Combine(temporaryFolder, "left-by-a-killed-write"), [1]);
        await using var app = BuildWithRoot(root.Path);

        // Act
        await StartupOf(app).StartAsync(CancellationToken.None);

        // Assert
        Directory.GetFileSystemEntries(temporaryFolder).ShouldBeEmpty();
    }

    [Test]
    public async Task a_start_with_a_root_that_cannot_be_made_still_starts()
    {
        // Arrange
        using var root = new TemporaryFileRoot();
        Directory.CreateDirectory(root.Path);
        var file = Path.Combine(root.Path, "a-file");
        await File.WriteAllBytesAsync(file, [1]);
        await using var app = BuildWithRoot(Path.Combine(file, "files"));

        // Act & Assert
        await Should.NotThrowAsync(() => StartupOf(app).StartAsync(CancellationToken.None));
    }

    [Test]
    [Arguments("nfs4")]
    [Arguments("cifs")]
    [Arguments("fuse.shfs")]
    public async Task a_start_with_a_root_on_a_network_mount_stops_and_names_the_mount(string type)
    {
        // Arrange
        using var root = new TemporaryFileRoot();
        await using var app = BuildWithRoot(root.Path, [new("/", type, "nas:/share")]);

        // Act
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => StartupOf(app).StartAsync(CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("file store root");
        failure.Message.ShouldContain(type);
        failure.Message.ShouldContain("nas:/share");
        Directory.Exists(root.Path).ShouldBeFalse();
    }

    [Test]
    public async Task a_start_with_a_link_to_a_folder_on_a_network_mount_stops()
    {
        // Arrange
        using var root = new TemporaryFileRoot();
        using var target = new TemporaryFileRoot();
        Directory.CreateDirectory(target.Path);
        Directory.CreateSymbolicLink(root.Path, target.Path);
        await using var app = BuildWithRoot(root.Path, [new(NetworkMountCheck.RealPathOf(target.Path), "nfs", "nas:/share")]);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => StartupOf(app).StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task a_start_with_a_root_on_a_local_mount_goes_on()
    {
        // Arrange
        using var root = new TemporaryFileRoot();
        await using var app = BuildWithRoot(root.Path, [new("/", "ext4", "/dev/sda1")]);

        // Act
        await StartupOf(app).StartAsync(CancellationToken.None);

        // Assert
        Directory.Exists(root.Path).ShouldBeTrue();
    }

    [Test]
    public async Task health_shows_a_root_that_cannot_take_a_write_with_a_200_and_alive_stays_healthy_and_a_repair_needs_no_restart()
    {
        // Arrange
        using var folder = new TemporaryFileRoot();
        Directory.CreateDirectory(folder.Path);
        var blocker = Path.Combine(folder.Path, "a-file");
        await File.WriteAllBytesAsync(blocker, [1]);
        await using var app = await StartServerAsync(Path.Combine(blocker, "files"));
        using var client = app.GetTestClient();

        // Act
        var whenBroken = await client.GetAsync("/health");
        var aliveWhenBroken = await client.GetAsync("/alive");
        File.Delete(blocker);
        var whenRepaired = await client.GetAsync("/health");

        // Assert
        ((int)whenBroken.StatusCode).ShouldBe(200);
        (await whenBroken.Content.ReadAsStringAsync()).ShouldBe("Degraded");
        (await aliveWhenBroken.Content.ReadAsStringAsync()).ShouldBe("Healthy");
        (await whenRepaired.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Test]
    public async Task the_health_check_makes_one_proof_in_its_interval_and_then_sees_a_root_that_broke()
    {
        // Arrange
        using var folder = new TemporaryFileRoot();
        var root = Path.Combine(folder.Path, "files");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = BuildWithRoot(root, clock: clock);
        var health = app.Services.GetRequiredService<HealthCheckService>();
        (await health.CheckHealthAsync()).Status.ShouldBe(HealthStatus.Healthy);
        Directory.Delete(folder.Path, recursive: true);
        await File.WriteAllBytesAsync(folder.Path, [1]);

        try
        {
            // Act
            var inTheInterval = await health.CheckHealthAsync();
            clock.Advance(TimeSpan.FromSeconds(30));
            var afterTheInterval = await health.CheckHealthAsync();
            var nextProbe = await health.CheckHealthAsync();

            // Assert
            inTheInterval.Status.ShouldBe(HealthStatus.Healthy);
            afterTheInterval.Status.ShouldBe(HealthStatus.Degraded);
            nextProbe.Status.ShouldBe(HealthStatus.Degraded);
            afterTheInterval.Entries["file-store"].Description.ShouldNotBeNull().ShouldContain("cannot take a write");
        }
        finally
        {
            File.Delete(folder.Path);
        }
    }

    private static async Task<WebApplication> StartServerAsync(string root)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration[$"{FileStoreOptions.SectionName}:Root"] = root;
        builder.Services.AddRouting();
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        builder.AddFileStore();
        var app = builder.Build();
        app.MapDefaultEndpoints();
        await app.StartAsync();
        return app;
    }

    private static IHostedService StartupOf(WebApplication app)
        => app.Services.GetServices<IHostedService>().Single(s => s.GetType().DeclaringType == typeof(FileStoreRegistration));

    private static WebApplication BuildWithRoot(string root, MountEntry[]? mounts = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration[$"{FileStoreOptions.SectionName}:Root"] = root;
        builder.AddFileStore();
        if (mounts is not null)
            builder.Services.AddSingleton<ReadMounts>(_ => Task.FromResult<IReadOnlyList<MountEntry>?>(mounts));
        if (clock is not null)
            builder.Services.AddSingleton(clock);
        return builder.Build();
    }
}
