namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Recall.Server.Auth;
using Shouldly;

public sealed record NativeTokens(string AccessToken, string RefreshToken, int ExpiresIn);

public sealed record NativeApp(string Redirect, string Verifier, string ClientState)
{
    public const string AppCallback = "recall://auth/callback";

    public string Challenge => Pkce.ChallengeOf(Verifier);

    public static NativeApp New(string redirect = AppCallback) => new(redirect, Secrets.New(), Secrets.New());

    public string StartUrl => $"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=app"
        + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={Challenge}&code_challenge_method=S256&state={ClientState}";
}

public sealed partial class AuthBrowser(AuthFlowFactory host, bool tls = false) : IDisposable
{
    private readonly HttpClient _client = host.NewClient();
    private readonly Dictionary<string, (string Value, string Path)> _cookies = [];

    public StubOidcProvider Provider => host.Provider;

    public string SessionCookieName => tls ? "__Host-recall" : "recall";

    public List<string> SetCookieLines { get; } = [];

    public IReadOnlyDictionary<string, (string Value, string Path)> Cookies => _cookies;

    public string? SessionCookie => _cookies.TryGetValue(SessionCookieName, out var cookie) ? cookie.Value : null;

    public void SetCookie(string name, string value, string path = "/") => _cookies[name] = (value, path);

    public void DropCookie(string name) => _cookies.Remove(name);

    public void Dispose() => _client.Dispose();

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var path = request.RequestUri!.IsAbsoluteUri ? request.RequestUri.AbsolutePath : request.RequestUri.OriginalString.Split('?')[0];
        var sent = _cookies.Where(cookie => path.StartsWith(cookie.Value.Path, StringComparison.Ordinal)).ToList();
        if (sent.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", sent.Select(cookie => $"{cookie.Key}={cookie.Value.Value}")));
        if (tls)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var response = await _client.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var lines))
        {
            foreach (var line in lines)
                Remember(line);
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url, bool csrf = false, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (csrf)
            request.Headers.Add("X-CSRF", "1");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return SendAsync(request);
    }

    public async Task<string> UserOfSessionAsync()
    {
        var me = await GetAsync("/auth/me", csrf: true);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sub").GetString()!;
    }

    public Task<HttpResponseMessage> PostFormAsync(string url, IEnumerable<(string Name, string Value)> fields, bool csrf = false, string? authorization = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))),
        };
        if (csrf)
            request.Headers.Add("X-CSRF", "1");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return SendAsync(request, cancellationToken);
    }

    public async Task<AuthorizeRequest> StartWebAsync(string? returnUrl = null)
    {
        var query = returnUrl is null ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var response = await GetAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=web{query}");
        return ToProvider(response);
    }

    public async Task<AuthorizeRequest> StartNativeAsync(NativeApp app) => ToProvider(await GetAsync(app.StartUrl));

    public Task<HttpResponseMessage> CallbackAsync(string state, string? code, string? iss = StubOidcProvider.Issuer, string? error = null)
    {
        var values = new Dictionary<string, string?> { ["state"] = state };
        if (code is not null)
            values["code"] = code;
        if (iss is not null)
            values["iss"] = iss;
        if (error is not null)
            values["error"] = error;
        return GetAsync(QueryHelpers.AddQueryString("/auth/oidc/callback", values));
    }

    public Task<HttpResponseMessage> FinishAtProviderAsync(AuthorizeRequest authorize, StubLogin login, string? iss = StubOidcProvider.Issuer)
        => CallbackAsync(authorize.State, Provider.Approve(authorize, login), iss);

    public async Task LoginWebAsync(string subject, string? returnUrl = null)
    {
        var response = await FinishAtProviderAsync(await StartWebAsync(returnUrl), new StubLogin(subject));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        SessionCookie.ShouldNotBeNull();
    }

    public async Task LoginWebAtOtherProviderAsync(string subject)
    {
        var authorize = ToProvider(await GetAsync($"/auth/oidc/{AuthFlowFactory.OtherModeId}/start?client=web"));
        var response = await CallbackAsync(authorize.State, host.OtherProvider!.Approve(authorize, new StubLogin(subject)), AuthFlowFactory.OtherIssuer);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        SessionCookie.ShouldNotBeNull();
    }

    public async Task<string> PendingCodeAsync(NativeApp app, string subject)
    {
        var page = await FinishAtProviderAsync(await StartNativeAsync(app), new StubLogin(subject));
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        return CodeInPage(await page.Content.ReadAsStringAsync());
    }

    public Task<HttpResponseMessage> ExchangeAsync(string code, string verifier, string redirectUri)
        => PostFormAsync("/auth/token",
        [
            ("grant_type", "authorization_code"),
            ("code", code),
            ("code_verifier", verifier),
            ("redirect_uri", redirectUri),
        ]);

    public async Task<NativeTokens> LoginNativeAsync(string subject)
    {
        var app = NativeApp.New();
        var code = await PendingCodeAsync(app, subject);
        var confirmed = await PostFormAsync("/auth/oidc/confirm", [("code", code)]);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return await TokensOf(await ExchangeAsync(code, app.Verifier, app.Redirect));
    }

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken, string? retryKey = null)
    {
        List<(string, string)> fields = [("grant_type", "refresh_token"), ("refresh_token", refreshToken)];
        if (retryKey is not null)
            fields.Add(("retry_key", retryKey));
        return PostFormAsync("/auth/token", fields);
    }

    public static async Task<NativeTokens> TokensOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("token_type").GetString().ShouldBe("Bearer");
        return new NativeTokens(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!, body.GetProperty("expires_in").GetInt32());
    }

    public static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
    }

    public static string CodeInPage(string html)
    {
        var code = CodeField().Match(html).Groups["code"].Value;
        code.ShouldNotBeNullOrEmpty();
        return code;
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static AuthorizeRequest ToProvider(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return AuthorizeRequest.Of(response.Headers.Location!);
    }

    private void Remember(string line)
    {
        SetCookieLines.Add(line);
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var name = parts[0][..parts[0].IndexOf('=', StringComparison.Ordinal)];
        var value = parts[0][(name.Length + 1)..];
        var path = parts.FirstOrDefault(part => part.StartsWith("path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "/";
        var expired = value.Length == 0;
        if (expired)
            _cookies.Remove(name);
        else
            _cookies[name] = (value, path);
    }

    [GeneratedRegex("name=\"code\" value=\"(?<code>[^\"]+)\"")]
    private static partial Regex CodeField();
}
