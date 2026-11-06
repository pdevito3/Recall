namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Users;
using Microsoft.Extensions.Configuration;
using Shouldly;

public sealed class FirstPartyClientOptionsTests
{
    [Test]
    public void swagger_client_is_first_party_when_it_is_set()
    {
        // Act
        var options = FromConfiguration(swaggerClientId: "swagger");

        // Assert
        options.Contains("app").ShouldBeTrue();
        options.Contains("swagger").ShouldBeTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void empty_swagger_client_id_is_not_first_party(string swaggerClientId)
    {
        // Act
        var options = FromConfiguration(swaggerClientId);

        // Assert
        options.Contains(swaggerClientId).ShouldBeFalse();
        options.ClientIds.ShouldBe(["recall", "app"], ignoreOrder: true);
    }

    [Test]
    public void with_no_settings_the_only_first_party_client_is_the_app_session()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var options = FirstPartyClientOptions.FromConfiguration(configuration);

        // Assert
        options.ClientIds.ShouldBe([FirstPartyClientOptions.AppClientId]);
    }

    private static FirstPartyClientOptions FromConfiguration(string swaggerClientId)
        => FirstPartyClientOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:ClientId"] = "app",
                ["Swagger:ClientId"] = swaggerClientId,
            })
            .Build());
}
