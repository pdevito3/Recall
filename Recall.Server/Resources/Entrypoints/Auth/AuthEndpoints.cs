namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Serilog;

public static class AuthEndpoints
{
    public sealed record StatusMode(string Kind, string Id, string DisplayName, string State, string? Detail, string StartUrl);

    public sealed record StatusAnswer(string Instance, string State, string CallbackUrl, string? Message, bool SetupRequired, IReadOnlyList<StatusMode> Modes);

    public sealed record MeAnswer(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sid")] string Sid,
        [property: JsonPropertyName("client_kind")] string ClientKind,
        [property: JsonPropertyName("login_name")] string? LoginName);

    public sealed record TokenAnswer(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn)
    {
        public override string ToString() => "[token answer]";
    }

    public sealed record LogoutAnswer([property: JsonPropertyName("provider_logout_url")] string ProviderLogoutUrl);

    private const string NoProviderDetail = "The provider did not answer.";

    /// <summary>A web or native session of a person. A personal access token is an app session too, and it does not pass.</summary>
    public static readonly AuthorizationPolicy InteractiveSessionOnly = new AuthorizationPolicyBuilder(AppSessionHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim(AppSessionHandler.ClientKindClaim, SessionKindText.Web, SessionKindText.Native)
        .Build();

    internal static readonly TimeSpan SlideWait = TimeSpan.FromSeconds(2);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthRoutes.Status, StatusAsync).AllowAnonymous().ExcludeFromDescription();
        // The page of a password writes a login attempt, so it has a limit. It has its own, so a shared address that opens
        // the page does not halve the logins that the start limit admits.
        endpoints.MapGet(AuthRoutes.Login, LoginAsync).RequireRateLimiting(AuthRegistration.PageLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Token, TokenAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Logout, LogoutAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.SignedOut, SignedOutReturn).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.RevokeOthers, RevokeOthersAsync).RequireAuthorization(InteractiveSessionOnly).ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.Me, MeAsync).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>
    /// A slide only moves the end of a session that is days away, so <c>/auth/me</c> waits at most
    /// <see cref="SlideWait"/> for it, and the person reads the journal while a write holds the line. A slide that has
    /// not ended by then is skipped, and the next <c>/auth/me</c> slides the session.
    /// </summary>
    public static async Task SlideAsync(HttpContext http, string sessionId)
    {
        var scopes = http.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var slide = Task.Run(() => SlideInItsOwnScopeAsync(scopes, sessionId), CancellationToken.None);
        DateTimeOffset? expires;
        try
        {
            expires = await slide.WaitAsync(SlideWait);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                          || DomainError.From(exception).Kind == DomainErrorKind.DatabaseBusy)
        {
            Telemetry.AddEvent([("exception.type", exception.GetType().Name)], "session.slide_skipped");
            if (exception is TimeoutException)
                _ = WarnOfALateFailureAsync(slide, sessionId);
            return;
        }

        if (expires is { } end && SessionCookie.Read(http.Request) is { } cookie)
            SessionCookie.Write(http.Response, http.Request, cookie, end);
    }

    // A token cannot stop a write that SQLite holds, such as a write on a disk that hangs, and the wait of SQLite blocks
    // its thread. So the slide runs on its own thread with its own scope, and it can end after the answer.
    private static async Task<DateTimeOffset?> SlideInItsOwnScopeAsync(IServiceScopeFactory scopes, string sessionId)
    {
        using var activity = Telemetry.Trace([("app.session_id", sessionId)], "session.slide");
        using var limit = new CancellationTokenSource(SlideWait);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SessionStore>().SlideWebAsync(sessionId, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.MarkFailed(activity, exception);
            throw;
        }
    }

    // The answer has gone, so this is the only code that sees how the slide ends. A cancel at its own limit is a skip.
    private static async Task WarnOfALateFailureAsync(Task slide, string sessionId)
    {
        try
        {
            await slide;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.ForContext(typeof(AuthEndpoints)).Here().Warning(exception,
                "The slide of session {SessionId} failed after /auth/me answered. A write that does not end can mean a disk that hangs",
                sessionId);
        }
    }

    private static async Task<IResult> StatusAsync(
        HttpContext http, LoginModes modes, SetupCode setupCode, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        var callbackUrl = AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback;
        var listed = await modes.ListAsync(cancellationToken);
        var states = await Task.WhenAll(listed.Select(mode => mode.ProbeAsync(cancellationToken)));
        var answers = listed.Zip(states, (mode, state) => new StatusMode(
            mode.Kind,
            mode.Id,
            mode.DisplayName,
            state == LoginModeState.Ready ? "ready" : "unreachable",
            state == LoginModeState.Ready ? null : NoProviderDetail,
            mode is PasswordLoginMode ? $"{AuthRoutes.Login}?client=app" : $"/auth/oidc/{Uri.EscapeDataString(mode.Id)}/start?client=app")).ToList();
        var allDown = states.All(state => state == LoginModeState.Unreachable);
        var message = allDown
            ? (listed.Count == 1 ? AuthPages.ProviderDown(listed[0].DisplayName) : "Sign-in is not available. Existing sessions keep working.")
            : null;
        var setupRequired = setupCode.IsLive && !await SetupCodeAtBoot.AnyLiveTenantAsync(scopes, cancellationToken);
        return Results.Json(new StatusAnswer("recall", allDown ? "degraded" : "ready", callbackUrl, message, setupRequired, answers));
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, LoginModes modes, SessionStore store, OwnerClaim ownerClaim, IServiceScopeFactory scopes, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        LoginClient? client = new LoginClient.Web(LocalReturnPath.Of(query["returnUrl"].Count == 1 ? query["returnUrl"][0] : null));
        // An app opens this page in the system browser with the values of its start (PWD-7).
        if (query.ContainsKey("client") && !LoginClient.TryParseStart(query, out client))
            return AuthPages.BadRequest();

        var signedOut = query.ContainsKey("signedOut");
        var hasError = query.ContainsKey("error");
        var isForPageOfAnotherPerson = query.ContainsKey("anotherPerson");

        if (client is LoginClient.Web web
            && !isForPageOfAnotherPerson
            && SessionCookie.Read(http.Request) is { } cookie
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is not null)
            return new AuthRedirect(web.ReturnPath);

        var listed = await modes.ListAsync(cancellationToken);
        var states = await Task.WhenAll(listed.Select(mode => mode.ProbeAsync(cancellationToken)));
        var pending = SessionCookie.PendingProviderLogoutOf(http.Request);
        var openSession = pending is null ? null : modes.FindOidc(pending.ModeId);
        var openSessionLogoutUrl = pending is null || openSession is null ? null : await openSession.ProviderLogoutUrlAsync(SignedOutUrlOf(http.Request), pending.State, cancellationToken);

        // A web page with a fresh claim file shows its offer, so it does not start the provider with no click (PWD-19).
        var offersOwnerClaim = client is LoginClient.Web && await PasswordEndpoints.OffersOwnerClaimAsync(ownerClaim, scopes, cancellationToken);
        var mayStartWithNoClick = listed is [OidcLoginMode] && states[0] == LoginModeState.Ready && !signedOut && !hasError && !isForPageOfAnotherPerson && openSessionLogoutUrl is null
            && !offersOwnerClaim;
        if (mayStartWithNoClick)
            return new AuthRedirect(AuthPages.StartUrl(listed[0].Id, client));

        var isSignedOut = (signedOut || openSession is not null) && SessionCookie.IsMarkedSignedOut(http.Request);
        var providerSession = isSignedOut && openSession is not null
            ? new AuthPages.OpenProviderSession(openSession.DisplayName, await openSession.ProbeAsync(cancellationToken), openSessionLogoutUrl)
            : null;
        var chooser = listed.Zip(states, (mode, state) => new AuthPages.ChooserMode(mode.Id, mode.DisplayName, state)).ToList();
        var passwordForm = listed.Contains(PasswordLoginMode.Instance)
            ? new AuthPages.PasswordForm(await PasswordEndpoints.StartFormAsync(http, store, timeProvider, client, null, cancellationToken), null, null)
            : null;
        var message = isForPageOfAnotherPerson ? (query.ContainsKey("wordsLost") ? AuthPages.AnotherPersonLostWordsMessage : AuthPages.AnotherPersonMessage) : hasError ? "The last sign-in did not finish." : null;
        return AuthPages.Chooser(chooser, client, isSignedOut, message, providerSession, asksTheProviderAgain: isForPageOfAnotherPerson, passwordForm,
            offersOwnerClaim: offersOwnerClaim);
    }

    private static AuthRedirect SignedOutReturn(HttpContext http, string? state)
    {
        if (state is not null && SessionCookie.PendingProviderLogoutOf(http.Request)?.State == state)
            SessionCookie.MarkSignedOut(http.Response, http.Request);
        return new AuthRedirect($"{AuthRoutes.Login}?signedOut=1");
    }

    private static string SignedOutUrlOf(HttpRequest request) => AuthRoutes.OriginOf(request) + AuthRoutes.SignedOut;

    private static async Task<IResult> TokenAsync(HttpContext http, SessionStore store, ITenantIdProvider tenants, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
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

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionStore store, LoginModes modes, CancellationToken cancellationToken)
    {
        var request = http.Request;
        var bearer = AppSessionHandler.CredentialOf(request.Headers.Authorization.ToString());
        var form = await AuthForm.ReadAsync(request, cancellationToken);
        _ = RefreshToken.TryParse(form is null ? null : AuthForm.OneValue(form, "refresh_token"), out var refresh);
        var csrf = AppSessionHandler.HasCsrfHeader(request);
        var cookie = SessionCookie.Read(request);

        if (cookie is not null && !csrf)
            return Results.Unauthorized();

        // The session ends even when the client goes away during the logout, so these reads take no cancellation token.
        if (bearer is not null
            && bearer.StartsWith(TokenPair.AccessPrefix, StringComparison.Ordinal)
            && await store.FindByAccessAsync(TokenHash.Of(bearer), SessionKind.Native, CancellationToken.None) is { } nativeSession)
            await store.RevokeAsync(nativeSession.SessionId, RevokeReason.Logout);

        if (cookie is not null
            && await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, CancellationToken.None) is { } webSession)
            await store.RevokeAsync(webSession.SessionId, RevokeReason.Logout);

        if (refresh is not null)
            await store.RevokeByRefreshAsync(refresh);

        if (csrf)
            SessionCookie.Clear(http.Response, request);
        // A browser that lost its cookie is still at the provider. The header proves that it is not a cross-site post.
        var isBrowser = csrf && (cookie is not null || (bearer is null && refresh is null));
        if (!isBrowser)
            return Results.NoContent();

        var modeOfSession = cookie is null ? null : await store.LoginModeOfWebSessionEvenIfEndedAsync(TokenHash.Of(cookie), cancellationToken);
        var mode = modeOfSession is null ? modes.OnlyOidc : modes.FindOidc(modeOfSession);
        if (mode is null)
        {
            SessionCookie.MarkSignedOut(http.Response, request);
            return Results.NoContent();
        }

        var pending = new SessionCookie.PendingProviderLogout(mode.Id, Secrets.New());
        // The two calls share one discovery while it runs, so a provider that is down costs one timeout and they read the same answer.
        var probe = mode.ProbeAsync(cancellationToken);
        var providerLogoutUrl = await mode.ProviderLogoutUrlAsync(SignedOutUrlOf(request), pending.State, cancellationToken);
        if (providerLogoutUrl is null && await probe == LoginModeState.Ready)
        {
            SessionCookie.MarkProviderSessionStays(http.Response, request, pending);
            return Results.NoContent();
        }

        SessionCookie.MarkProviderLogoutUnfinished(http.Response, request, pending);
        return providerLogoutUrl is null ? Results.NoContent() : Results.Json(new LogoutAnswer(providerLogoutUrl));
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext http, SessionStore store)
    {
        await store.RevokeOthersAsync(http.User.FindFirst("sub")!.Value, http.User.FindFirst(AppSessionHandler.SessionIdClaim)!.Value);
        return Results.NoContent();
    }

    private static bool IsPlainVisitor(HttpRequest request)
        => AppSessionHandler.CredentialOf(request.Headers.Authorization.ToString()) is null
            && (SessionCookie.Read(request) is null || AppSessionHandler.HasCsrfHeader(request));

    private static async Task<IResult> MeAsync(HttpContext http, ISender sender, PasswordLogin passwords, CancellationToken cancellationToken)
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
            await SlideAsync(http, sessionId);

        // The SPA shows the change of a password only to a person whose password it can change (PWD-17). A token cannot change it.
        var loginName = kind is SessionKindText.Web or SessionKindText.Native
            ? await passwords.ChangeableLoginNameOfAsync(http.User.FindFirst("sub")!.Value, cancellationToken)
            : null;
        return Results.Json(new MeAnswer(current.Identifier, current.Name, sessionId, kind, loginName));
    }

    private static IResult TokenError(string error)
        => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private sealed record TokenOutcome(TokenPair? Pair, string? Error);
}
