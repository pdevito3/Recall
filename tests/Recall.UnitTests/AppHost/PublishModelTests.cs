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
    public async Task a_publish_has_no_storage_container()
    {
        // Act
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--operation", "publish", "--publisher", "default", "--AuthProvider=Keycloak"], (options, _) => options.DisableDashboard = true);

        // Assert
        builder.ExecutionContext.IsPublishMode.ShouldBeTrue();
        builder.Resources.ShouldNotContain(resource => resource.Name == "storage");
    }

    [Test]
    public async Task a_publish_copies_the_spa_build_into_the_web_root_of_the_server()
    {
        // Act
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--operation", "publish", "--publisher", "default", "--AuthProvider=Keycloak"], (options, _) => options.DisableDashboard = true);

        // Assert
        var server = builder.Resources.Single(resource => resource.Name == "server");
        var copy = server.Annotations.OfType<ContainerFilesDestinationAnnotation>().ShouldHaveSingleItem();
        copy.Source.Name.ShouldBe("webfrontend");
        copy.DestinationPath.ShouldBe("wwwroot");
    }

    [Test]
    public async Task the_stack_starts_no_second_host()
    {
        // Act
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), [], (options, _) => options.DisableDashboard = true);

        // Assert
        builder.Resources.ShouldNotContain(resource => resource.Name == "bff");
    }

    [Test]
    public async Task the_none_provider_forwards_the_login_settings_of_the_apphost()
    {
        // Arrange
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation),
            ["--AuthProvider=None", "--Auth:Providers:0:Issuer=https://idp.example", "--Auth:Providers:0:ClientSecret=secret", "--Auth:OpenSignup=true"],
            (options, _) => options.DisableDashboard = true);

        // Act
        var environment = await ServerEnvironment(builder);

        // Assert
        builder.Resources.OfType<ContainerResource>().ShouldBeEmpty();
        environment["Auth__Providers__0__Issuer"].ShouldBe("https://idp.example");
        environment["Auth__Providers__0__ClientSecret"].ShouldBe("secret");
        environment["Auth__OpenSignup"].ShouldBe("true");
        environment.ShouldNotContainKey("Auth__Authority");
    }

    [Test]
    public async Task the_none_provider_with_no_settings_gives_the_server_no_login_method()
    {
        // Arrange
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--AuthProvider=None"], (options, _) => options.DisableDashboard = true);

        // Act
        var environment = await ServerEnvironment(builder);

        // Assert
        environment.Keys.ShouldNotContain(key => key.StartsWith("Auth__", StringComparison.Ordinal));
    }

    [Test]
    public async Task a_provider_is_both_the_login_method_and_the_jwt_authority_of_the_server()
    {
        // Arrange
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            typeof(Isolation), ["--AuthProvider=DuendeDemo"], (options, _) => options.DisableDashboard = true);

        // Act
        var environment = await ServerEnvironment(builder);

        // Assert
        environment["Auth__Providers__0__Id"].ShouldBe("duendedemo");
        environment["Auth__Providers__0__DisplayName"].ShouldBe("DuendeDemo");
        environment["Auth__Providers__0__AllowHttp"].ShouldBe(bool.FalseString);
        environment["Auth__Providers__0__ClientId"].ShouldBe(environment["Auth__ClientId"]);
        environment["Auth__Providers__0__ClientSecret"].ShouldBe(environment["Auth__ClientSecret"]);
        ((ReferenceExpression)environment["Auth__Providers__0__Issuer"]).ValueExpression.ShouldBe("https://demo.duendesoftware.com");
        ((ReferenceExpression)environment["Auth__Authority"]).ValueExpression.ShouldBe("https://demo.duendesoftware.com");
    }

    private static async Task<Dictionary<string, object>> ServerEnvironment(IDistributedApplicationTestingBuilder builder)
    {
        var server = builder.Resources.Single(resource => resource.Name == "server");
        var environment = new Dictionary<string, object>();
        foreach (var callback in server.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(new EnvironmentCallbackContext(builder.ExecutionContext, server, environment));

        return environment;
    }
}
