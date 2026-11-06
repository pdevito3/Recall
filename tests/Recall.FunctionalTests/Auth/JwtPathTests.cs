namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
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
    public async Task a_dpop_proof_for_https_passes_behind_a_proxy_that_sends_x_forwarded_proto()
    {
        // Arrange
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = BoundToken(key);
        using var behindProxy = new AuthBrowser(AuthHosts.Open, tls: true);

        // Act
        var response = await SendWithProofAsync(behindProxy, key, token, $"https://localhost{ProtectedPath}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task a_dpop_proof_for_https_is_refused_with_no_forwarded_proto()
    {
        // Arrange
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = BoundToken(key);
        using var direct = new AuthBrowser(AuthHosts.Open);

        // Act
        var response = await SendWithProofAsync(direct, key, token, $"https://localhost{ProtectedPath}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string BoundToken(ECDsa key)
    {
        var thumbprint = Base64UrlEncoder.Encode(new JsonWebKey(JsonSerializer.Serialize(PublicJwk(key))).ComputeJwkThumbprint());
        return GoodToken(new Dictionary<string, object>
        {
            ["sub"] = "provider-person",
            ["client_id"] = "machine",
            ["cnf"] = new Dictionary<string, object> { ["jkt"] = thumbprint },
        });
    }

    private static Task<HttpResponseMessage> SendWithProofAsync(AuthBrowser browser, ECDsa key, string token, string htu)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProtectedPath);
        request.Headers.TryAddWithoutValidation("Authorization", $"DPoP {token}");
        request.Headers.TryAddWithoutValidation("DPoP", Proof(key, token, htu));
        return browser.SendAsync(request);
    }

    private static string Proof(ECDsa key, string token, string htu)
    {
        var header = Encode(new Dictionary<string, object> { ["typ"] = "dpop+jwt", ["alg"] = "ES256", ["jwk"] = PublicJwk(key) });
        var payload = Encode(new Dictionary<string, object>
        {
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["htm"] = "GET",
            ["htu"] = htu,
            ["iat"] = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(),
            ["ath"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(token))),
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256);
        return $"{header}.{payload}.{Base64UrlEncoder.Encode(signature)}";
    }

    private static string Encode(Dictionary<string, object> value)
        => Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(value));

    private static Dictionary<string, object> PublicJwk(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return new Dictionary<string, object>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64UrlEncoder.Encode(parameters.Q.X),
            ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y),
        };
    }
}
