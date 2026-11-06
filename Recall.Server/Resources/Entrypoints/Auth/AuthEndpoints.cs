namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;

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
        endpoints.MapGet(AuthRoutes.Me, MeAsync).AllowAnonymous().ExcludeFromDescription();
    }

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

        if (SessionCookie.Read(http.Request) is { } cookie
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is not null)
            return new AuthRedirect(returnPath);

        if (modes.All.Count == 0)
            return AuthPages.NoMethod(AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback);

        var states = await Task.WhenAll(modes.All.Select(mode => mode.ProbeAsync(cancellationToken)));
        if (modes.All.Count == 1 && states[0] == LoginModeState.Ready && !signedOut && !hasError)
            return new AuthRedirect($"/auth/oidc/{Uri.EscapeDataString(modes.All[0].Id)}/start?client=web&returnUrl={Uri.EscapeDataString(returnPath)}");

        var isSignedOut = signedOut && SessionCookie.IsMarkedSignedOut(http.Request);
        var chooser = await Task.WhenAll(modes.All.Zip(states, async (mode, state) => new AuthPages.ChooserMode(
            mode.Id, mode.DisplayName, state, isSignedOut ? await mode.ProviderLogoutUrlAsync(cancellationToken) : null)));
        return AuthPages.Chooser(chooser, returnPath, isSignedOut, hasError ? "The last sign-in did not finish." : null);
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
        if (form["retry_key"].Count > 1 || !RetryKey.TryParse(AuthForm.OneValue(form, "retry_key"), out var retryKey))
            return new TokenOutcome(null, "invalid_request");
        if (AuthForm.OneValue(form, "refresh_token") is not { } refreshText)
            return new TokenOutcome(null, "invalid_request");
        if (!RefreshToken.TryParse(refreshText, out var token))
            return new TokenOutcome(null, "invalid_grant");

        var result = await store.RefreshAsync(token, retryKey, cancellationToken);
        return new TokenOutcome(result.Pair, result.Pair is null ? "invalid_grant" : null);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        var request = http.Request;
        var bearer = AppSessionHandler.CredentialOf(request.Headers.Authorization.ToString());
        var form = await AuthForm.ReadAsync(request, cancellationToken);
        _ = RefreshToken.TryParse(form is null ? null : AuthForm.OneValue(form, "refresh_token"), out var refresh);
        var csrf = AppSessionHandler.HasCsrfHeader(request);
        var cookie = SessionCookie.Read(request);

        // A cookie needs the header. A 204 would say that this ended a session that it left alive.
        if (cookie is not null && !csrf)
            return Results.Unauthorized();

        if (bearer is not null
            && bearer.StartsWith(TokenPair.AccessPrefix, StringComparison.Ordinal)
            && await store.FindByAccessAsync(TokenHash.Of(bearer), SessionKind.Native, cancellationToken) is { } nativeSession)
            await store.RevokeAsync(nativeSession.SessionId, RevokeReason.Logout, cancellationToken);

        if (csrf && cookie is not null
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is { } webSession)
            await store.RevokeAsync(webSession.SessionId, RevokeReason.Logout, cancellationToken);

        if (refresh is not null)
            await store.RevokeByRefreshAsync(refresh, cancellationToken);

        if (csrf)
            SessionCookie.Clear(http.Response, request);
        if (csrf && cookie is not null)
            SessionCookie.MarkSignedOut(http.Response, request);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        await store.RevokeOthersAsync(http.User.FindFirst("sub")!.Value, http.User.FindFirst(AppSessionHandler.SessionIdClaim)!.Value, cancellationToken);
        return Results.NoContent();
    }

    private static bool IsPlainVisitor(HttpRequest request)
        => AppSessionHandler.CredentialOf(request.Headers.Authorization.ToString()) is null
            && (SessionCookie.Read(request) is null || AppSessionHandler.HasCsrfHeader(request));

    private static async Task<IResult> MeAsync(HttpContext http, ISender sender, SessionStore store, CancellationToken cancellationToken)
    {
        var session = await http.AuthenticateAsync(AppSessionHandler.SchemeName);
        if (!session.Succeeded)
        {
            return IsPlainVisitor(http.Request) ? Results.NoContent() : Results.Challenge(authenticationSchemes: [AppSessionHandler.SchemeName]);
        }

        http.User = session.Principal;
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
