namespace Recall.IntegrationTests.Resources.Storage;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        Directory.Exists(temporaryFolder).ShouldBeFalse();
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

        // Act
        await StartupOf(app).StartAsync(CancellationToken.None);

        // Assert
        File.Exists(file).ShouldBeTrue();
    }

    private static IHostedService StartupOf(WebApplication app)
        => app.Services.GetServices<IHostedService>().Single(s => s.GetType().DeclaringType == typeof(FileStoreRegistration));

    private static WebApplication BuildWithRoot(string root)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration[$"{FileStoreOptions.SectionName}:Root"] = root;
        builder.AddFileStore();
        return builder.Build();
    }
}
