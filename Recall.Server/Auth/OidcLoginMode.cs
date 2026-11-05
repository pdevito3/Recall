namespace Recall.Server.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Databases;
using Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Resources;
using Serilog;

public delegate HttpMessageHandler OidcHandlerFactory(OidcProviderOptions provider);

public sealed record OidcDiscovery(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string JwksUri,
    string? UserinfoEndpoint,
    IReadOnlyList<string> TokenEndpointAuthMethods,
    bool AdvertisesIssParameter,
    bool AcceptsPromptLogin,
    string? EndSessionEndpoint);

public sealed record OidcCallback(string? Code, string? Iss, string? Error)
{
    public override string ToString() => "[oidc callback]";
}

public sealed record RedeemResult(ExternalIdentity? Identity, LoginFailure? Failure)
{
    public static RedeemResult Failed(LoginFailure failure) => new(null, failure);
}

public sealed class LoginModes(IOptions<AuthOptions> options, OidcHandlerFactory handlers, TimeProvider timeProvider) : IDisposable
{
    private readonly List<OidcLoginMode> _modes = options.Value.Providers
        .Select((provider, position) => new OidcLoginMode(
            AuthOptions.ModeIdOf(provider, position), provider, handlers(provider), timeProvider))
        .ToList();

    public IReadOnlyList<ILoginMode> All => _modes;

    public OidcLoginMode? FindOidc(string? id) => _modes.FirstOrDefault(mode => mode.Id == id);

    public void Dispose()
    {
        foreach (var mode in _modes)
            mode.Dispose();
    }
}

/// <summary>The client has no retry, because a retry of a code redemption fails and burns the code.</summary>
public sealed class OidcLoginMode : ILoginMode, IDisposable
{
    private const string Scope = "openid profile email";

    private static readonly TimeSpan ProbeCache = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] AllowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly ILogger _logger = Log.ForContext<OidcLoginMode>();
    private readonly OidcProviderOptions _provider;
    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _probeGate = new();
    private (Task<OidcDiscovery?> Task, long StartedAt)? _probe;

    public OidcLoginMode(string id, OidcProviderOptions provider, HttpMessageHandler handler, TimeProvider timeProvider)
    {
        Id = id;
        _provider = provider;
        _timeProvider = timeProvider;
        _http = new HttpClient(handler) { Timeout = CallTimeout };
        DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? id : provider.DisplayName;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Kind => "oidc";

    public void Dispose() => _http.Dispose();

    public static SocketsHttpHandler DefaultHandler(OidcProviderOptions provider)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (!string.IsNullOrWhiteSpace(provider.CaCertificatePath))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(provider.CaCertificatePath);
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
        }

        return handler;
    }

    public async Task<LoginModeState> ProbeAsync(CancellationToken cancellationToken)
        => await DiscoverAsync(cancellationToken) is null ? LoginModeState.Unreachable : LoginModeState.Ready;

    public async Task<string?> ProviderLogoutUrlAsync(CancellationToken cancellationToken)
    {
        if ((await DiscoverAsync(cancellationToken))?.EndSessionEndpoint is not { } endpoint)
            return null;

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{endpoint}{separator}client_id={Uri.EscapeDataString(_provider.ClientId!)}";
    }

    public Task<OidcDiscovery?> DiscoverAsync(CancellationToken cancellationToken)
    {
        Task<OidcDiscovery?> running;
        lock (_probeGate)
        {
            if (_probe is not { } probe
                || (probe.Task.IsCompleted && _timeProvider.GetElapsedTime(probe.StartedAt) >= ProbeCache))
            {
                probe = (FetchDiscoveryAsync(), _timeProvider.GetTimestamp());
                _probe = probe;
            }

            running = probe.Task;
        }

        return running.WaitAsync(cancellationToken);
    }

    public string BuildAuthorizeUrl(OidcDiscovery discovery, string state, string nonce, string verifier, string redirectUri, bool promptLogin = false)
    {
        var separator = discovery.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return discovery.AuthorizationEndpoint + separator
            + "response_type=code"
            + "&scope=" + Uri.EscapeDataString(Scope)
            + "&client_id=" + Uri.EscapeDataString(_provider.ClientId!)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&state=" + Uri.EscapeDataString(state)
            + "&nonce=" + Uri.EscapeDataString(nonce)
            + "&code_challenge=" + Pkce.ChallengeOf(verifier)
            + "&code_challenge_method=S256"
            + (promptLogin && discovery.AcceptsPromptLogin ? "&prompt=login" : "");
    }

    public async Task<RedeemResult> RedeemAsync(LoginAttempt attempt, OidcCallback callback, CancellationToken cancellationToken)
    {
        if (callback.Error is { } error)
            return RedeemResult.Failed(error switch
            {
                "access_denied" => LoginFailure.AccessDenied,
                "temporarily_unavailable" or "server_error" => LoginFailure.IdpUnreachable,
                _ => LoginFailure.InvalidResponse,
            });

        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery is null)
            return RedeemResult.Failed(LoginFailure.IdpUnreachable);

        if (callback.Iss is { } iss ? !string.Equals(iss, discovery.Issuer, StringComparison.Ordinal) : discovery.AdvertisesIssParameter)
            return Refuse("the iss of the authorization response does not name this provider");

        if (string.IsNullOrEmpty(callback.Code))
            return Refuse("the authorization response has no code");

        try
        {
            var tokens = await ExchangeCodeAsync(discovery, attempt, callback.Code, cancellationToken);
            if (tokens is not { IdToken: { } idToken })
                return Refuse("the token endpoint refused the code");

            var keys = new JsonWebKeySet(await _http.GetStringAsync(discovery.JwksUri, cancellationToken));
            return await IdentityOfAsync(discovery, attempt, idToken, tokens.AccessToken, keys, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.Here().Warning("Provider {ModeId} stopped answering during a login", Id);
            return RedeemResult.Failed(LoginFailure.IdpUnreachable);
        }
        // A charset that .NET does not know fails the read with InvalidOperationException.
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return Refuse("the provider sent a body that is not valid");
        }
    }

    private RedeemResult Refuse(string reason)
    {
        _logger.Here().Warning("Refused a login of provider {ModeId}: {Reason}", Id, reason);
        return RedeemResult.Failed(LoginFailure.InvalidResponse);
    }

    private async Task<OidcDiscovery?> FetchDiscoveryAsync()
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var url = _provider.Issuer!.TrimEnd('/') + "/.well-known/openid-configuration";
            using var response = await _http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Here().Warning("Provider {ModeId} answered discovery with status {Status}", Id, (int)response.StatusCode);
                return null;
            }

            var discovery = ParseDiscovery(await response.Content.ReadAsStringAsync(timeout.Token));
            if (discovery is null)
                _logger.Here().Warning("Provider {ModeId} sent a discovery document that this server does not accept", Id);
            return discovery;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Here().Warning("Provider {ModeId} did not answer discovery: {Failure}", Id, exception.GetType().Name);
            return null;
        }
    }

    private OidcDiscovery? ParseDiscovery(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var issuer = Text(root, "issuer");
        var authorization = Text(root, "authorization_endpoint");
        var token = Text(root, "token_endpoint");
        var jwks = Text(root, "jwks_uri");
        var userinfo = Text(root, "userinfo_endpoint");
        // A trailing slash is the one difference that providers show between the setting and the document.
        if (issuer is null || issuer.TrimEnd('/') != _provider.Issuer!.TrimEnd('/'))
            return null;
        if (!IsAllowedUrl(authorization) || !IsAllowedUrl(token) || !IsAllowedUrl(jwks) || (userinfo is not null && !IsAllowedUrl(userinfo)))
            return null;

        var methods = root.TryGetProperty("token_endpoint_auth_methods_supported", out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList()
            : [];
        var advertisesIss = root.TryGetProperty("authorization_response_iss_parameter_supported", out var iss) && iss.ValueKind == JsonValueKind.True;
        // OpenID Connect defines prompt=login for each provider, and the list is optional. So only a list without login stops it.
        var acceptsPromptLogin = !root.TryGetProperty("prompt_values_supported", out var prompts)
            || prompts.ValueKind != JsonValueKind.Array
            || prompts.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "login");
        return new OidcDiscovery(issuer, authorization!, token!, jwks!, userinfo, methods, advertisesIss, acceptsPromptLogin, EndSessionEndpointOrNoLink(root));
    }

    private string? EndSessionEndpointOrNoLink(JsonElement root)
    {
        var endSession = Text(root, "end_session_endpoint");
        return IsAllowedUrl(endSession) ? endSession : null;
    }

    private bool IsAllowedUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && _provider.AllowHttp));

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private sealed record ProviderTokens(string? IdToken, string? AccessToken)
    {
        public override string ToString() => "[provider tokens]";
    }

    private async Task<ProviderTokens?> ExchangeCodeAsync(
        OidcDiscovery discovery, LoginAttempt attempt, string code, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = attempt.RedirectUri,
            ["code_verifier"] = attempt.PkceVerifier,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var methods = discovery.TokenEndpointAuthMethods;
        if (methods.Count == 0 || methods.Contains("client_secret_basic"))
        {
            var pair = $"{Uri.EscapeDataString(_provider.ClientId!)}:{Uri.EscapeDataString(_provider.ClientSecret!)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        else
        {
            form["client_id"] = _provider.ClientId!;
            if (methods.Contains("client_secret_post"))
                form["client_secret"] = _provider.ClientSecret!;
        }

        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException("The token endpoint failed.", null, response.StatusCode);
        // The body of a refusal can repeat what the request sent, so it is not read or logged.
        if (!response.IsSuccessStatusCode)
            return null;

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new ProviderTokens(Text(body.RootElement, "id_token"), Text(body.RootElement, "access_token"));
    }

    private async Task<RedeemResult> IdentityOfAsync(
        OidcDiscovery discovery, LoginAttempt attempt, string idToken, string? accessToken, JsonWebKeySet keys, CancellationToken cancellationToken)
    {
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer,
            ValidAudience = _provider.ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = AllowedAlgorithms,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
        });
        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken token)
            return Refuse($"the ID token failed its check ({validation.Exception?.GetType().Name})");

        var hasAzp = token.TryGetPayloadValue<string>("azp", out var azp);
        if (hasAzp ? azp != _provider.ClientId : token.Audiences.Skip(1).Any())
            return Refuse("the ID token has several audiences and no azp of this client, or the azp of another client");

        if (!token.TryGetPayloadValue<string>("nonce", out var nonce) || !SameText(nonce, attempt.Nonce))
            return Refuse("the nonce of the ID token does not match");

        var subject = token.Subject;
        if (string.IsNullOrEmpty(subject) || subject.Length > ExternalLogin.SubjectMaxLength || !StorableText.CanStore(subject))
            return Refuse("the subject of the ID token is not valid");

        var claims = new Dictionary<string, string?>
        {
            ["given_name"] = ClaimOf(token, "given_name"),
            ["family_name"] = ClaimOf(token, "family_name"),
            ["name"] = ClaimOf(token, "name"),
            ["email"] = ClaimOf(token, "email"),
            ["preferred_username"] = ClaimOf(token, "preferred_username"),
        };

        if (discovery.UserinfoEndpoint is { } endpoint && accessToken is not null
            && await MergeUserinfoAsync(endpoint, accessToken, subject, claims, cancellationToken) == UserinfoSubject.Other)
            return Refuse("the subject of userinfo is not the subject of the ID token");

        var profile = new Profile(claims["given_name"], claims["family_name"], claims["name"], claims["email"], claims["preferred_username"]);
        return new RedeemResult(new ExternalIdentity(discovery.Issuer, subject, profile), null);
    }

    private enum UserinfoSubject
    {
        NotDifferent,
        Other,
    }

    private static string? ClaimOf(JsonWebToken token, string name)
        => token.TryGetPayloadValue<string>(name, out var value) ? value : null;

    private async Task<UserinfoSubject> MergeUserinfoAsync(
        string endpoint, string accessToken, string subject, Dictionary<string, string?> claims, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return UserinfoSubject.NotDifferent;

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return UserinfoSubject.NotDifferent;
            if (Text(root, "sub") is not { } sub)
                return UserinfoSubject.NotDifferent;
            if (sub != subject)
                return UserinfoSubject.Other;

            foreach (var name in claims.Keys.ToList())
            {
                if (Text(root, name) is { } value)
                    claims[name] = value;
            }

            return UserinfoSubject.NotDifferent;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return UserinfoSubject.NotDifferent;
        }
    }

    private static bool SameText(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
