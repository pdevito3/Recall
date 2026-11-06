namespace Recall.UnitTests.AppHost;

using Aspire.Hosting.ApplicationModel;
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
    public async Task a_run_with_no_container_gives_the_bff_no_container_endpoint()
    {
        // Arrange
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--AuthProvider=DuendeDemo"], (options, _) => options.DisableDashboard = true);
        builder.Resources.OfType<ContainerResource>().ShouldBeEmpty();
        var bff = builder.Resources.Single(resource => resource.Name == "bff");
        var environment = new Dictionary<string, object>();

        // Act
        foreach (var callback in bff.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(new EnvironmentCallbackContext(builder.ExecutionContext, bff, environment));

        // Assert
        environment["Bff__AdditionalAllowedHosts"].ShouldBeOfType<ReferenceExpression>().ValueProviders.ShouldBeEmpty();
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
