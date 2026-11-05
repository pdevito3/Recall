namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Recall.Server.Auth;

public sealed record StubLogin(string Subject)
{
    public string Name { get; init; } = "Ada Lovelace";
    public string? IdTokenIssuer { get; init; }
    public string? Nonce { get; init; }
    public string? UserinfoSubject { get; init; }
    public string? TokenBody { get; init; }
}

public sealed record AuthorizeRequest(string State, string Nonce, string CodeChallenge, string RedirectUri, string ClientId)
{
    public static AuthorizeRequest Of(Uri location)
    {
        var query = QueryHelpers.ParseQuery(location.Query);
        return new AuthorizeRequest(query["state"]!, query["nonce"]!, query["code_challenge"]!, query["redirect_uri"]!, query["client_id"]!);
    }
}

public sealed class StubOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "recall-test-client";
    public const string JwtAudience = "test-api";

    private static readonly string[] AuthMethods = ["client_secret_basic"];

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "stub-key" };
    private readonly ConcurrentDictionary<string, (AuthorizeRequest Request, StubLogin Login)> _codes = new();
    private readonly ConcurrentDictionary<string, StubLogin> _accessTokens = new();
    private readonly ConcurrentQueue<string> _handedOut = new();

    public string ClientSecret { get; } = Secrets.New();

    public bool Down { get; set; }

    public IReadOnlyCollection<string> HandedOut => _handedOut;

    public string Approve(AuthorizeRequest request, StubLogin login)
    {
        var code = Secrets.New();
        _codes[code] = (request, login);
        _handedOut.Enqueue(code);
        return code;
    }

    public OpenIdConnectConfiguration JwtConfiguration()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(_signingKey);
        return configuration;
    }

    public string SignJwt(IReadOnlyDictionary<string, object> claims, string issuer = Issuer, string audience = JwtAudience)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims.ToDictionary(),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
            throw new HttpRequestException("The stub provider is down.");

        return request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = $"{Issuer}/authorize",
                ["token_endpoint"] = $"{Issuer}/token",
                ["jwks_uri"] = $"{Issuer}/jwks",
                ["userinfo_endpoint"] = $"{Issuer}/userinfo",
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["authorization_response_iss_parameter_supported"] = true,
            }),
            "/jwks" => Json(new Dictionary<string, object> { ["keys"] = new[] { Jwk() } }),
            "/token" => await TokenAsync(request, cancellationToken),
            "/userinfo" => Userinfo(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private Dictionary<string, string> Jwk()
    {
        var parameters = _signingKey.Rsa.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = _signingKey.KeyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(ClientId)}:{Uri.EscapeDataString(ClientSecret)}"));
        if (request.Headers.Authorization is not { Scheme: "Basic" } authorization || authorization.Parameter != basic)
            return Refused("invalid_client");

        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (!_codes.TryRemove(form["code"].ToString(), out var grant)
            || form["grant_type"] != "authorization_code"
            || form["redirect_uri"] != grant.Request.RedirectUri
            || Pkce.ChallengeOf(form["code_verifier"].ToString()) != grant.Request.CodeChallenge)
            return Refused("invalid_grant");

        var login = grant.Login;
        if (login.TokenBody is { } tokenBody)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenBody, Encoding.UTF8, "application/json") };

        var accessToken = Secrets.New();
        _accessTokens[accessToken] = login;
        var idToken = SignJwt(
            new Dictionary<string, object>
            {
                ["sub"] = login.Subject,
                ["name"] = login.Name,
                ["nonce"] = login.Nonce ?? grant.Request.Nonce,
            },
            login.IdTokenIssuer ?? Issuer,
            ClientId);
        _handedOut.Enqueue(accessToken);
        _handedOut.Enqueue(idToken);
        return Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
        });
    }

    private HttpResponseMessage Userinfo(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is not { } token || !_accessTokens.TryGetValue(token, out var login))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        return Json(new Dictionary<string, object> { ["sub"] = login.UserinfoSubject ?? login.Subject, ["name"] = login.Name });
    }

    private static HttpResponseMessage Refused(string error)
        => Json(new Dictionary<string, object> { ["error"] = error }, HttpStatusCode.BadRequest);

    private static HttpResponseMessage Json(Dictionary<string, object> body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
