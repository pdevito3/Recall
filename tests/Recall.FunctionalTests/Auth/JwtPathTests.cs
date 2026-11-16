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
    [Arguments("applicationId", "recall")]
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
    [Property("Rule", "PAT-15")]
    [Arguments("client_id")]
    [Arguments("azp")]
    [Arguments("applicationId")]
    public async Task a_token_whose_client_is_a_personal_access_token_is_refused(string claim)
    {
        // Arrange
        var token = GoodToken(new Dictionary<string, object> { ["sub"] = "provider-person", [claim] = "pat_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f" });

        // Act
        var status = await StatusWithBearerAsync(token);

        // Assert
        status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Property("Rule", "PAT-15")]
    public async Task a_token_with_the_app_client_in_applicationId_is_refused_at_the_current_user()
    {
        // Arrange
        var token = GoodToken(new Dictionary<string, object> { ["sub"] = "provider-person", ["applicationId"] = "recall" });
        using var browser = new AuthBrowser(AuthHosts.Open);

        // Act
        var response = await browser.GetAsync("/api/v1/users/me", authorization: $"Bearer {token}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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
