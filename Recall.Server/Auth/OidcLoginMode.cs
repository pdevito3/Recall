namespace Recall.Server.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Databases;
using Domain.LoginProviders;
using Domain.Tenants;
using Entities;
using Microsoft.EntityFrameworkCore;
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

/// <summary>
/// The OIDC modes of the install: each entry of <c>Auth:Providers</c>, then each Usable row of <c>auth_oidc_providers</c> in the order of
/// <c>created_at</c> (IDP-1). The list is one value that a reload replaces whole, so a request reads either the old list or the new one.
/// A reload keeps the mode of a row whose settings did not change, with its discovery cache. It never disposes a replaced mode, because a
/// login in flight can still hold it. The client of a replaced mode closes its idle connections after the idle time of its pool, and the
/// collector takes it.
/// </summary>
public sealed class LoginModes : IDisposable
{
    private readonly OidcHandlerFactory _handlers;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<OidcProviderOptions> _environment;
    private readonly IReadOnlyList<OidcLoginMode> _environmentModes;
    private readonly bool _allowLoopback;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<LoginModes>();
    private volatile Snapshot _snapshot;

    public LoginModes(IOptions<AuthOptions> options, OidcHandlerFactory handlers, TimeProvider timeProvider, IServiceScopeFactory scopeFactory)
    {
        _handlers = handlers;
        _timeProvider = timeProvider;
        _scopeFactory = scopeFactory;
        _environment = options.Value.Providers;
        _allowLoopback = options.Value.AllowLoopbackProviders;
        _environmentModes = _environment
            .Select((provider, position) => new OidcLoginMode(
                AuthOptions.ModeIdOf(provider, position), provider, handlers(provider), timeProvider))
            .ToList();
        _snapshot = Snapshot.Of(_environmentModes, _environment, []);
    }

    public IReadOnlyList<OidcLoginMode> Oidc => _snapshot.Modes;

    /// <summary>The entries of <c>Auth:Providers</c>. An entry wins over a row with its ID or its issuer (IDP-3).</summary>
    public IReadOnlyList<OidcProviderOptions> Environment => _environment;

    /// <summary>
    /// The issuers of each mode of <see cref="Oidc"/>, each with and with no trailing slash, because a link holds the issuer that the
    /// discovery document gave. The last-Admin rule counts a link only while one of these has its issuer (IDP-14).
    /// </summary>
    public IReadOnlyList<string> UsableIssuers => _snapshot.Issuers;

    /// <summary>The issuers that <see cref="UsableIssuers"/> would hold after a delete of the row with this ID.</summary>
    public IReadOnlyList<string> UsableIssuersWithout(string rowId)
        => Snapshot.IssuersOf(_environment, _snapshot.Rows.Where(row => row.Key.Id != rowId));

    /// <summary>
    /// Each mode of the login page and of <c>/auth/status</c>. The password mode comes first when the install has no OIDC
    /// provider, or when a live user holds a password credential (PWD-1). So an install with providers and no password
    /// keeps the start with no click of a single provider.
    /// </summary>
    public async Task<IReadOnlyList<ILoginMode>> ListAsync(CancellationToken cancellationToken)
    {
        var modes = _snapshot.Modes;
        return modes.Count == 0 || await AnyLivePasswordAsync(cancellationToken) ? [PasswordLoginMode.Instance, .. modes] : modes;
    }

    public OidcLoginMode? FindOidc(string? id) => _snapshot.Modes.FirstOrDefault(mode => mode.Id == id);

    public OidcLoginMode? OnlyOidc => _snapshot.Modes is [var only] ? only : null;

    /// <summary>
    /// Reads the rows again and replaces the list. Each save and each delete of a row calls it after its save, and the boot calls it after
    /// the migrations. Reloads run one at a time, and each reads the rows after the save that called it, so the last one to end holds
    /// each committed change.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reload.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateSystemScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<LoginProviderSecret>();
            var rows = await dbContext.LoginProviders
                .AsNoTracking()
                .TagWithOperationCallSite("auth.modes.rows")
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .ToListAsync(cancellationToken);

            var current = _snapshot.Rows;
            var rowModes = new List<RowMode>();
            foreach (var row in rows)
            {
                var secret = secrets.Read(row.ProtectedClientSecret);
                if (row.StateOf(_environment, secret is not null) != LoginProviderState.Usable)
                    continue;

                var settings = row.OptionsWith(secret!, _allowLoopback);
                var key = RowKey.Of(settings);
                if (current.FirstOrDefault(kept => kept.Key == key) is { } same)
                    rowModes.Add(same);
                else if (NewMode(settings) is { } mode)
                    rowModes.Add(new RowMode(key, mode));
            }

            _snapshot = Snapshot.Of(_environmentModes, _environment, rowModes);
        }
        finally
        {
            _reload.Release();
        }
    }

    private OidcLoginMode? NewMode(OidcProviderOptions settings)
    {
        try
        {
            return new OidcLoginMode(settings.Id!, settings, _handlers(settings), _timeProvider);
        }
        // The add checked the CA file, and the file can go later. The row then has no button until the file is back and a save reloads.
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.Here().Warning("Provider {ModeId} is not on the login page, because the server cannot read its CA file", settings.Id);
            return null;
        }
    }

    /// <summary>The settings that make a mode. A mode is kept while each of them stays.</summary>
    private sealed record RowKey(string Id, string DisplayName, string Issuer, string ClientId, string ClientSecret, bool AllowHttp, string? CaCertificatePath)
    {
        public static RowKey Of(OidcProviderOptions settings)
            => new(settings.Id!, settings.DisplayName!, settings.Issuer!, settings.ClientId!, settings.ClientSecret!, settings.AllowHttp, settings.CaCertificatePath);

        public override string ToString() => $"[provider settings {Id}]";
    }

    private sealed record RowMode(RowKey Key, OidcLoginMode Mode);

    private sealed record Snapshot(IReadOnlyList<OidcLoginMode> Modes, IReadOnlyList<RowMode> Rows, IReadOnlyList<string> Issuers)
    {
        public static Snapshot Of(IReadOnlyList<OidcLoginMode> environmentModes, IReadOnlyList<OidcProviderOptions> environment, IReadOnlyList<RowMode> rows)
            => new([.. environmentModes, .. rows.Select(row => row.Mode)], rows, IssuersOf(environment, rows));

        public static List<string> IssuersOf(IEnumerable<OidcProviderOptions> environment, IEnumerable<RowMode> rows)
            => environment.Select(entry => entry.Issuer).Concat(rows.Select(row => row.Key.Issuer))
                .OfType<string>()
                .SelectMany(issuer => new[] { issuer.TrimEnd('/'), issuer.TrimEnd('/') + "/" })
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }

    private async Task<bool> AnyLivePasswordAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Set<PasswordCredential>()
            .TagWithOperationCallSite("auth.modes.any_password")
            .AnyAsync(p => dbContext.Users.Any(u => u.Id == p.UserId), cancellationToken);
    }

    public void Dispose()
    {
        foreach (var mode in _snapshot.Modes)
            mode.Dispose();
        _reload.Dispose();
    }
}

/// <summary>The client has no retry, because a retry of a code redemption fails and burns the code.</summary>
public sealed class OidcLoginMode : ILoginMode, IDisposable
{
    private const string Scope = "openid profile email";

    private const string SubjectClaim = "sub";

    // Far above what a provider sends. An ID token over 250 KB already fails its check, and a discovery or key document is a few KB.
    private const int AnswerLimitBytes = 1024 * 1024;

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
        _http = new HttpClient(handler) { Timeout = CallTimeout, MaxResponseContentBufferSize = AnswerLimitBytes };
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
            var roots = CaCertificateFile.Read(provider.CaCertificatePath)
                ?? throw new CryptographicException("The CA file of the provider holds no PEM certificate that this server can read.");
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
        }

        // Each connect checks the addresses it resolves, so a redirect to a refused address fails as the first request would.
        if (provider.RefusesLoopback)
            handler.ConnectCallback = ConnectToAllowedAddressAsync;

        return handler;
    }

    /// <summary>A loopback, link-local, or unspecified address. A private range, such as an IdP on the LAN, is allowed.</summary>
    public static bool IsRefusedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && (bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static async ValueTask<Stream> ConnectToAllowedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken))
            .Where(address => !IsRefusedAddress(address))
            .ToArray();
        if (addresses.Length == 0)
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The issuer names a loopback, link-local, or unspecified address, which a provider from the app cannot use.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<LoginModeState> ProbeAsync(CancellationToken cancellationToken)
        => await DiscoverAsync(cancellationToken) is null ? LoginModeState.Unreachable : LoginModeState.Ready;

    public async Task<string?> ProviderLogoutUrlAsync(string returnUrl, string state, CancellationToken cancellationToken)
    {
        if ((await DiscoverAsync(cancellationToken))?.EndSessionEndpoint is not { } endpoint)
            return null;

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{endpoint}{separator}client_id={Uri.EscapeDataString(_provider.ClientId!)}&post_logout_redirect_uri={Uri.EscapeDataString(returnUrl)}&state={Uri.EscapeDataString(state)}";
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

        var answer = "token";
        try
        {
            var tokens = await ExchangeCodeAsync(discovery, attempt, callback.Code, cancellationToken);
            if (tokens.IdToken is not { } idToken)
                return Refuse("the token answer has no id_token");

            answer = "key";
            using var jwksRequest = new HttpRequestMessage(HttpMethod.Get, discovery.JwksUri);
            using var jwks = await SendAsync(jwksRequest, cancellationToken);
            jwks.EnsureSuccessStatusCode();
            var keys = await ReadAnswerAsync(jwks, text => new JsonWebKeySet(text).GetSigningKeys(), cancellationToken);
            return await IdentityOfAsync(discovery, attempt, idToken, tokens.Bearer, keys, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.Here().Warning("Provider {ModeId} failed its {Answer} answer during a login: {Cause}", Id, answer, CauseOf(exception));
            return RedeemResult.Failed(LoginFailure.IdpUnreachable);
        }
        catch (NotValidAnswerException exception)
        {
            return Refuse($"the {answer} answer is not valid: {CauseOf(exception)}");
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => AsAnswerAsync(() => _http.SendAsync(request, cancellationToken));

    private static Task<T> ReadAnswerAsync<T>(HttpResponseMessage response, Func<string, T> parse, CancellationToken cancellationToken)
        => AsAnswerAsync(async () => parse(await response.Content.ReadAsStringAsync(cancellationToken)));

    /// <summary>
    /// Each answer of a provider comes through here, from the send, which reads and decompresses the body, to the last value that a
    /// parser takes from it. The provider chooses the charset, the encoding, and the bytes, and these fail with many types. So each such
    /// failure leaves here as one type and no caller lists them. The limit of the client counts the decoded bytes, because the handler
    /// decompresses before the client buffers, and an answer over it is one such failure.
    /// </summary>
    private static async Task<T> AsAnswerAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception exception) when (exception is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded }
            or not (HttpRequestException or OperationCanceledException))
        {
            throw new NotValidAnswerException(exception);
        }
    }

    /// <summary>The text that a log can carry about a failed answer. A status, a kind, or a type, and never a message, because a message can repeat what the provider sent.</summary>
    private static string CauseOf(Exception exception) => exception switch
    {
        NotValidAnswerException { InnerException: { } cause } => CauseOf(cause),
        HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded } => "over a size limit of this server",
        HttpRequestException { StatusCode: { } status } => $"status {(int)status}",
        HttpRequestException { HttpRequestError: var kind and not HttpRequestError.Unknown } => kind.ToString(),
        OperationCanceledException => "no answer in time",
        _ => exception.GetBaseException().GetType().Name,
    };

    private sealed class NotValidAnswerException(Exception cause) : Exception(null, cause);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Here().Warning("Provider {ModeId} answered discovery with status {Status}", Id, (int)response.StatusCode);
                return null;
            }

            var discovery = await ReadAnswerAsync(response, ParseDiscovery, timeout.Token);
            if (discovery is null)
                _logger.Here().Warning("Provider {ModeId} sent a discovery document that this server does not accept", Id);
            return discovery;
        }
        // Each caller shares this probe and none owns it, so a fault here would take down the chooser for each provider.
        catch (Exception exception)
        {
            _logger.Here().Warning("Provider {ModeId} failed its discovery answer: {Cause}", Id, CauseOf(exception));
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

    private static Dictionary<string, string> TextsOf(string json, IEnumerable<string> names)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return names
            .Select(name => (Name: name, Value: Text(root, name)))
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Name, field => field.Value!);
    }

    private sealed record ProviderTokens(string? IdToken, AuthenticationHeaderValue? Bearer)
    {
        // The header refuses a token that it cannot carry, so that token fails here with each other fault of the answer.
        public static ProviderTokens Of(string json)
        {
            var texts = TextsOf(json, ["id_token", "access_token"]);
            return new ProviderTokens(
                texts.GetValueOrDefault("id_token"),
                texts.TryGetValue("access_token", out var token) ? new AuthenticationHeaderValue("Bearer", token) : null);
        }

        public override string ToString() => "[provider tokens]";
    }

    private async Task<ProviderTokens> ExchangeCodeAsync(
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
        using var response = await SendAsync(request, cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException("The token endpoint failed.", null, response.StatusCode);
        // The body of a refusal can repeat what the request sent, so it is not read or logged.
        if (!response.IsSuccessStatusCode)
            throw new NotValidAnswerException(new HttpRequestException(null, null, response.StatusCode));

        return await ReadAnswerAsync(response, ProviderTokens.Of, cancellationToken);
    }

    private async Task<RedeemResult> IdentityOfAsync(
        OidcDiscovery discovery, LoginAttempt attempt, string idToken, AuthenticationHeaderValue? bearer, IList<SecurityKey> keys, CancellationToken cancellationToken)
    {
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = discovery.Issuer,
            ValidAudience = _provider.ClientId,
            IssuerSigningKeys = keys,
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

        if (discovery.UserinfoEndpoint is { } endpoint && bearer is not null
            && await MergeUserinfoAsync(endpoint, bearer, subject, claims, cancellationToken) == UserinfoSubject.Other)
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
        string endpoint, AuthenticationHeaderValue bearer, string subject, Dictionary<string, string?> claims, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = bearer;
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return UnusedUserinfo($"status {(int)response.StatusCode}");

            // The subject comes first and alone, so a claim that cannot be read cannot hide another person.
            var subjects = await ReadAnswerAsync(response, text => TextsOf(text, [SubjectClaim]), cancellationToken);
            if (!subjects.TryGetValue(SubjectClaim, out var sub))
                return UnusedUserinfo($"no {SubjectClaim}");
            if (sub != subject)
                return UserinfoSubject.Other;

            var texts = await ReadAnswerAsync(response, text => TextsOf(text, claims.Keys), cancellationToken);
            foreach (var name in claims.Keys.Where(texts.ContainsKey).ToList())
                claims[name] = texts[name];

            return UserinfoSubject.NotDifferent;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotValidAnswerException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return UnusedUserinfo(CauseOf(exception));
        }
    }

    private UserinfoSubject UnusedUserinfo(string cause)
    {
        _logger.Here().Warning("Provider {ModeId} sent a userinfo answer that this server did not use, so the login goes on with the ID token: {Cause}", Id, cause);
        return UserinfoSubject.NotDifferent;
    }

    private static bool SameText(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
