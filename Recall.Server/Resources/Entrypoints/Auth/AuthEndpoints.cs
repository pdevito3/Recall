namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;

/// <summary>The contract for a client of any kind. <c>docs/authentication.md</c> states it, because these routes are not in the OpenAPI document.</summary>
public static class AuthEndpoints
{
    public sealed record StatusMode(string Kind, string Id, string DisplayName, string State, string? Detail, string StartUrl);

    public sealed record StatusAnswer(string Instance, string State, string CallbackUrl, string? Message, bool SetupRequired, IReadOnlyList<StatusMode> Modes);

    public sealed record MeAnswer(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sid")] string Sid,
        [property: JsonPropertyName("client_kind")] string ClientKind);

    public sealed record TokenAnswer(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn)
    {
        public override string ToString() => "[token answer]";
    }

    private const string NoProviderDetail = "The provider did not answer.";

    public static readonly AuthorizeAttribute AppSessionOnly = new() { AuthenticationSchemes = AppSessionHandler.SchemeName };

    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthRoutes.Status, StatusAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.Login, LoginAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Token, TokenAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Logout, LogoutAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.RevokeOthers, RevokeOthersAsync).RequireAuthorization(AppSessionOnly).ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.Me, MeAsync).RequireAuthorization(AppSessionOnly).ExcludeFromDescription();
    }

    /// <summary>Extends a web session, at most once a day. Only this call and <c>/bff/user</c> write, so that an API call never does.</summary>
    public static async Task SlideAsync(HttpContext http, SessionStore store, string sessionId, CancellationToken cancellationToken)
    {
        if (await store.SlideWebAsync(sessionId, cancellationToken) is { } expires && SessionCookie.Read(http.Request) is { } cookie)
            SessionCookie.Write(http.Response, http.Request, cookie, expires);
    }

    private static async Task<IResult> StatusAsync(HttpContext http, LoginModes modes, CancellationToken cancellationToken)
    {
        var callbackUrl = AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback;
        if (modes.All.Count == 0)
            return Results.Json(new StatusAnswer("recall", "no_login_method", callbackUrl, AuthPages.NoMethodMessage, true, []));

        var states = await Task.WhenAll(modes.All.Select(mode => mode.ProbeAsync(cancellationToken)));
        var answers = modes.All.Zip(states, (mode, state) => new StatusMode(
            mode.Kind,
            mode.Id,
            mode.DisplayName,
            state == LoginModeState.Ready ? "ready" : "unreachable",
            state == LoginModeState.Ready ? null : NoProviderDetail,
            $"/auth/oidc/{Uri.EscapeDataString(mode.Id)}/start?client=app")).ToList();
        var allDown = states.All(state => state == LoginModeState.Unreachable);
        var message = allDown
            ? (modes.All.Count == 1 ? AuthPages.ProviderDown(modes.All[0].DisplayName) : "Sign-in is not available. Existing sessions keep working.")
            : null;
        return Results.Json(new StatusAnswer("recall", allDown ? "degraded" : "ready", callbackUrl, message, false, answers));
    }

    private static async Task<IResult> LoginAsync(HttpContext http, LoginModes modes, SessionStore store, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        var returnPath = LocalReturnPath.Of(query["returnUrl"].Count == 1 ? query["returnUrl"][0] : null);
        var signedOut = query.ContainsKey("signedOut");
        var hasError = query.ContainsKey("error");

        // A navigation has no X-CSRF header, so this reads the cookie itself
        if (!signedOut && SessionCookie.Read(http.Request) is { } cookie
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is not null)
            return new AuthRedirect(returnPath);

        if (modes.All.Count == 0)
            return AuthPages.NoMethod(AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback);

        var states = await Task.WhenAll(modes.All.Select(mode => mode.ProbeAsync(cancellationToken)));
        if (modes.All.Count == 1 && states[0] == LoginModeState.Ready && !signedOut && !hasError)
            return new AuthRedirect($"/auth/oidc/{Uri.EscapeDataString(modes.All[0].Id)}/start?client=web&returnUrl={Uri.EscapeDataString(returnPath)}");

        var chooser = modes.All.Zip(states, (mode, state) => new AuthPages.ChooserMode(mode.Id, mode.DisplayName, state)).ToList();
        return AuthPages.Chooser(chooser, returnPath, signedOut, hasError ? "The last sign-in did not finish." : null);
    }

    private static async Task<IResult> TokenAsync(HttpContext http, SessionStore store, ITenantIdProvider tenants, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var form = await AuthForm.ReadAsync(http.Request, cancellationToken);
        if (form is null)
            return TokenError("invalid_request");

        var pair = AuthForm.OneValue(form, "grant_type") switch
        {
            "authorization_code" => await ExchangeCodeAsync(form, store, tenants, cancellationToken),
            "refresh_token" => await RefreshAsync(form, store, cancellationToken),
            _ => new TokenOutcome(null, "invalid_request"),
        };
        if (pair.Pair is not { } tokens)
            return TokenError(pair.Error!);

        var remaining = (tokens.AccessExpiresAt - timeProvider.GetUtcNow()).TotalSeconds;
        return Results.Json(new TokenAnswer(tokens.AccessToken, tokens.RefreshToken, "Bearer", (int)Math.Ceiling(Math.Max(0, remaining))));
    }

    private static async Task<TokenOutcome> ExchangeCodeAsync(IFormCollection form, SessionStore store, ITenantIdProvider tenants, CancellationToken cancellationToken)
    {
        if (AuthForm.OneValue(form, "code") is not { } code
            || AuthForm.OneValue(form, "code_verifier") is not { } verifier
            || AuthForm.OneValue(form, "redirect_uri") is not { } redirectUri)
            return new TokenOutcome(null, "invalid_request");

        var found = await store.ConsumeAppCodeAsync(TokenHash.Of(code), cancellationToken);
        if (found is null
            || !string.Equals(found.RedirectUri, redirectUri, StringComparison.Ordinal)
            || !Pkce.Matches(verifier, found.Challenge)
            || await tenants.FindLiveUserAsync(found.UserId, cancellationToken) is null)
            return new TokenOutcome(null, "invalid_grant");

        return new TokenOutcome(await store.StartNativeAsync(found.UserId, found.LoginMode, cancellationToken), null);
    }

    private static async Task<TokenOutcome> RefreshAsync(IFormCollection form, SessionStore store, CancellationToken cancellationToken)
    {
        if (!RetryKey.TryParse(AuthForm.OneValue(form, "retry_key"), out var retryKey))
            return new TokenOutcome(null, "invalid_request");
        if (!RefreshToken.TryParse(AuthForm.OneValue(form, "refresh_token"), out var token))
            return new TokenOutcome(null, "invalid_grant");

        var result = await store.RefreshAsync(token, retryKey, cancellationToken);
        return new TokenOutcome(result.Pair, result.Pair is null ? "invalid_grant" : null);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        if (AppSessionHandler.CredentialOf(http.Request.Headers.Authorization.ToString()) is { } bearer
            && bearer.StartsWith(TokenPair.AccessPrefix, StringComparison.Ordinal)
            && await store.FindByAccessAsync(TokenHash.Of(bearer), SessionKind.Native, cancellationToken) is { } nativeSession)
            await store.RevokeAsync(nativeSession.SessionId, RevokeReason.Logout, cancellationToken);

        if (AppSessionHandler.HasCsrfHeader(http.Request) && SessionCookie.Read(http.Request) is { } cookie
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is { } webSession)
            await store.RevokeAsync(webSession.SessionId, RevokeReason.Logout, cancellationToken);

        if (await AuthForm.ReadAsync(http.Request, cancellationToken) is { } form
            && RefreshToken.TryParse(AuthForm.OneValue(form, "refresh_token"), out var token))
            await store.RevokeByRefreshAsync(token, cancellationToken);

        SessionCookie.Clear(http.Response, http.Request);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        await store.RevokeOthersAsync(http.User.FindFirst("sub")!.Value, http.User.FindFirst(AppSessionHandler.SessionIdClaim)!.Value, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(HttpContext http, ISender sender, SessionStore store, CancellationToken cancellationToken)
    {
        var sessionId = http.User.FindFirst(AppSessionHandler.SessionIdClaim)!.Value;
        var kind = http.User.FindFirst(AppSessionHandler.ClientKindClaim)!.Value;
        var current = await sender.Send(new GetCurrentUser.Query(), cancellationToken);
        if (kind == SessionKindText.Web)
            await SlideAsync(http, store, sessionId, cancellationToken);

        return Results.Json(new MeAnswer(current.Identifier, current.Name, sessionId, kind));
    }

    private static IResult TokenError(string error)
        => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private sealed record TokenOutcome(TokenPair? Pair, string? Error);
}
