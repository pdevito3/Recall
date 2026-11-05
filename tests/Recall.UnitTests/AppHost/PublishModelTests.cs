namespace Recall.UnitTests.AppHost;

using Aspire.Hosting.Testing;
using Recall.AppHost;
using Shouldly;

[NotInParallel(TestConstraints.HostEnvironment)]
public class PublishModelTests
{
    [Test]
    public async Task a_run_starts_no_storage_container()
    {
        // Act
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), [], (options, _) => options.DisableDashboard = true);

        // Assert
        builder.Resources.ShouldNotContain(resource => resource.Name == "storage");
    }

    [Test]
    public async Task a_publish_has_no_storage_container()
    {
        // Act
        // ZeroFactor, the default provider, refuses a publish
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--operation", "publish", "--publisher", "default", "--AuthProvider=Keycloak"], (options, _) => options.DisableDashboard = true);

        // Assert
        builder.ExecutionContext.IsPublishMode.ShouldBeTrue();
        builder.Resources.ShouldNotContain(resource => resource.Name == "storage");
    }
}
