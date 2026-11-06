namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Recall.Server.Auth;

public enum IdTokenSigning
{
    ProviderKey,
    OtherKeyWithTheSameId,
    None,
    Hs256,
}

public sealed record StubLogin(string Subject)
{
    public string Name { get; init; } = "Ada Lovelace";
    public string? IdTokenIssuer { get; init; }
    public string? Nonce { get; init; }
    public string? UserinfoSubject { get; init; }
    public bool UserinfoOmitsSubject { get; init; }
    public string? UserinfoName { get; init; }
    public string? TokenBody { get; init; }
    public string? TokenCharset { get; init; }
    public string? UserinfoCharset { get; init; }
    public string? UserinfoBody { get; init; }
    public string? AccessToken { get; init; }
    public bool TokenBodyIsCorrupt { get; init; }
    public bool UserinfoBodyIsCorrupt { get; init; }
    public string[]? IdTokenAudiences { get; init; }
    public string? IdTokenAzp { get; init; }
    public TimeSpan IdTokenLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public bool IdTokenHasNoExpiry { get; init; }
    public IdTokenSigning IdTokenSigning { get; init; }
}

public sealed record AuthorizeRequest(string State, string Nonce, string CodeChallenge, string RedirectUri, string ClientId, string? Prompt = null)
{
    public static AuthorizeRequest Of(Uri location)
    {
        var query = QueryHelpers.ParseQuery(location.Query);
        return new AuthorizeRequest(query["state"]!, query["nonce"]!, query["code_challenge"]!, query["redirect_uri"]!, query["client_id"]!, query.TryGetValue("prompt", out var prompt) ? prompt.ToString() : null);
    }
}

public sealed class StubOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "recall-test-client";
    public const string JwtAudience = "test-api";

    private static readonly string[] AuthMethods = ["client_secret_basic"];

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "stub-key" };
    private readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "stub-key" };
    private readonly SymmetricSecurityKey _hmacKey = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "stub-hmac" };
    private readonly ConcurrentDictionary<string, (AuthorizeRequest Request, StubLogin Login)> _codes = new();
    private readonly ConcurrentDictionary<string, StubLogin> _accessTokens = new();
    private readonly ConcurrentQueue<string> _handedOut = new();
    private readonly string _issuer;

    public StubOidcProvider(string issuer = Issuer)
    {
        _issuer = issuer;

        // RSA.Create makes the pair at its first use. On macOS two threads that race for that use can each make a pair.
        _ = _signingKey.Rsa.ExportParameters(false);
        _ = _otherKey.Rsa.ExportParameters(false);
    }

    public string ClientSecret { get; } = Secrets.New();

    public string? DiscoveryCharset { get; set; }

    public string? JwksCharset { get; set; }

    public bool DiscoveryBodyIsCorrupt { get; set; }

    public bool JwksBodyIsCorrupt { get; set; }

    public bool Down { get; set; }

    public string[]? PromptValues { get; init; }

    /// <summary>Changes each answer on its way out, for a fault of a real provider. It gets the path and the answer that the stub would send.</summary>
    public Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>>? AnswerFault { get; set; }

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

        var path = request.RequestUri!.AbsolutePath;
        var answer = path switch
        {
            "/.well-known/openid-configuration" => Discovery(),
            "/jwks" => JwksBodyIsCorrupt ? CorruptAnswer() : WithCharset(JwksCharset, Json(new Dictionary<string, object> { ["keys"] = new[] { Jwk(), HmacJwk() } })),
            "/token" => await TokenAsync(request, cancellationToken),
            "/userinfo" => Userinfo(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return AnswerFault is { } fault ? await fault(path, answer, cancellationToken) : answer;
    }

    private HttpResponseMessage Discovery()
    {
        if (DiscoveryBodyIsCorrupt)
            return CorruptAnswer();

        var body = new Dictionary<string, object>
        {
            ["issuer"] = _issuer,
            ["authorization_endpoint"] = $"{_issuer}/authorize",
            ["token_endpoint"] = $"{_issuer}/token",
            ["jwks_uri"] = $"{_issuer}/jwks",
            ["userinfo_endpoint"] = $"{_issuer}/userinfo",
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
            ["authorization_response_iss_parameter_supported"] = true,
        };
        if (PromptValues is { } values)
            body["prompt_values_supported"] = values;
        return WithCharset(DiscoveryCharset, Json(body));
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

    // A real provider has no reason to publish this. It gives an HS256 token a key to pass with, once a change loosens both the algorithms and the key list.
    private Dictionary<string, string> HmacJwk() => new()
    {
        ["kty"] = "oct",
        ["use"] = "sig",
        ["alg"] = "HS256",
        ["kid"] = _hmacKey.KeyId,
        ["k"] = Base64UrlEncoder.Encode(_hmacKey.Key),
    };

    private string IdTokenOf(StubLogin login, string nonce)
    {
        var claims = new Dictionary<string, object> { ["sub"] = login.Subject, ["name"] = login.Name, ["nonce"] = nonce };
        if (login.IdTokenAudiences is { } audiences)
            claims["aud"] = audiences;
        if (login.IdTokenAzp is { } azp)
            claims["azp"] = azp;

        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var expires = now + login.IdTokenLifetime;
        var issuedAt = expires < now ? expires.AddMinutes(-5) : now;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = login.IdTokenIssuer ?? _issuer,
            Audience = login.IdTokenAudiences is null ? ClientId : null,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = login.IdTokenHasNoExpiry ? null : expires,
            Claims = claims,
            SigningCredentials = login.IdTokenSigning switch
            {
                IdTokenSigning.ProviderKey => new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
                IdTokenSigning.OtherKeyWithTheSameId => new SigningCredentials(_otherKey, SecurityAlgorithms.RsaSha256),
                IdTokenSigning.Hs256 => new SigningCredentials(_hmacKey, SecurityAlgorithms.HmacSha256),
                _ => null,
            },
        });
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
        if (login.TokenBodyIsCorrupt)
            return CorruptAnswer();

        if (login.TokenBody is { } tokenBody)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenBody, Encoding.UTF8, "application/json") };

        var accessToken = login.AccessToken ?? Secrets.New();
        _accessTokens[accessToken] = login;
        var idToken = IdTokenOf(login, login.Nonce ?? grant.Request.Nonce);
        _handedOut.Enqueue(accessToken);
        _handedOut.Enqueue(idToken);
        return WithCharset(login.TokenCharset, Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
        }));
    }

    private HttpResponseMessage Userinfo(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is not { } token || !_accessTokens.TryGetValue(token, out var login))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        if (login.UserinfoBodyIsCorrupt)
            return CorruptAnswer();

        if (login.UserinfoBody is { } userinfoBody)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(userinfoBody, Encoding.UTF8, "application/json") };

        var body = new Dictionary<string, object> { ["name"] = login.UserinfoName ?? login.Name };
        if (!login.UserinfoOmitsSubject)
            body["sub"] = login.UserinfoSubject ?? login.Subject;

        return WithCharset(login.UserinfoCharset, Json(body));
    }

    /// <summary>The stream that the real handler makes for an answer with a gzip encoding, so that the buffer of HttpClient fails in the same way.</summary>
    private static HttpResponseMessage CorruptAnswer() => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new GZipStream(new MemoryStream("this is not gzip at all"u8.ToArray()), CompressionMode.Decompress)),
    };

    private static HttpResponseMessage WithCharset(string? charset, HttpResponseMessage response)
    {
        if (charset is not null)
            response.Content.Headers.ContentType!.CharSet = charset;
        return response;
    }

    private static HttpResponseMessage Refused(string error)
        => Json(new Dictionary<string, object> { ["error"] = error }, HttpStatusCode.BadRequest);

    private static HttpResponseMessage Json(Dictionary<string, object> body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
