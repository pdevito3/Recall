namespace Recall.FunctionalTests.Auth;

using System.Net;
using Shouldly;

public class JwtPathTests
{
    private const string ProtectedPath = "/api/v1/tenants";

    private static StubOidcProvider Provider => AuthHosts.Open.Provider;

    private static string GoodToken(Dictionary<string, object>? claims = null)
        => Provider.SignJwt(claims ?? new Dictionary<string, object> { ["sub"] = "provider-person", ["client_id"] = "machine" });

    private static async Task<HttpStatusCode> StatusWithBearerAsync(string token)
    {
        using var browser = new AuthBrowser(AuthHosts.Open);
        return (await browser.GetAsync(ProtectedPath, authorization: $"Bearer {token}")).StatusCode;
    }

    [Test]
    public async Task a_token_of_the_provider_passes_authentication()
    {
        // Arrange
        var token = GoodToken();

        // Act
        var status = await StatusWithBearerAsync(token);

        // Assert
        status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_token_from_another_issuer_is_refused()
    {
        // Arrange
        var token = Provider.SignJwt(new Dictionary<string, object> { ["sub"] = "provider-person" }, issuer: "https://other.test");

        // Act
        var status = await StatusWithBearerAsync(token);

        // Assert
        status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("client_id", "recall")]
    [Arguments("azp", "recall")]
    [Arguments("sub", "usr_0123456789")]
    public async Task a_token_that_names_an_app_session_is_refused(string claim, string value)
    {
        // Arrange
        var token = GoodToken(new Dictionary<string, object> { ["sub"] = "provider-person", [claim] = value });

        // Act
        var status = await StatusWithBearerAsync(token);

        // Assert
        status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_token_with_a_cnf_claim_is_refused()
    {
        // Arrange
        var token = GoodToken(new Dictionary<string, object>
        {
            ["sub"] = "provider-person",
            ["client_id"] = "machine",
            ["cnf"] = new Dictionary<string, object> { ["jkt"] = "thumbprint" },
        });

        // Act
        var status = await StatusWithBearerAsync(token);

        // Assert
        status.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
