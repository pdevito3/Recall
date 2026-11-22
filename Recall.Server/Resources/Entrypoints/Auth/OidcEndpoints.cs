namespace Recall.Server.Resources.Entrypoints.Auth;

using Microsoft.AspNetCore.Authentication;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Resources;

public static class OidcEndpoints
{
    private static readonly string[] HtmlTypes = ["text/html"];

    public static void MapOidcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthRoutes.Start, StartAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Link, StartLinkAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.Callback, CallbackAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.Confirm, ConfirmAsync).AllowAnonymous().ExcludeFromDescription();
    }

    private static async Task<IResult> StartAsync(string id, HttpContext http, LoginModes modes, SessionStore store, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (modes.FindOidc(id) is not { } mode)
            return AuthPages.NotFound();

        var redirectUri = AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback;
        if (!LoginClient.TryParseStart(http.Request.Query, out var client) || redirectUri.Length > LoginAttempt.RedirectUriMaxLength)
            return AuthPages.BadRequest();

        if (await mode.DiscoverAsync(cancellationToken) is not { } discovery)
            return ProviderDown(http.Request, mode.DisplayName);

        var state = Secrets.New();
        var nonce = Secrets.New();
        var verifier = Secrets.New();
        var binding = Secrets.New();
        var stateHash = TokenHash.Of(state);
        var expiresAt = timeProvider.GetUtcNow() + SessionPolicy.AttemptLifetime;
        var native = client as LoginClient.Native;
        await store.AddAttemptAsync(new LoginAttempt
        {
            StateHash = stateHash.Hex,
            BindingHash = TokenHash.Of(binding).Hex,
            ModeId = mode.Id,
            Nonce = nonce,
            PkceVerifier = verifier,
            RedirectUri = redirectUri,
            ClientKind = native is null ? SessionKind.Web : SessionKind.Native,
            ReturnPath = (client as LoginClient.Web)?.ReturnPath,
            NativeRedirect = native?.Redirect.Value,
            NativeChallenge = native?.Challenge,
            NativeState = native?.ClientState,
            ExpiresAt = expiresAt,
        }, cancellationToken);

        BindingCookie.Write(http.Response, http.Request, stateHash, binding, expiresAt);
        var promptLogin = OneValue(http.Request.Query, "prompt") == AuthPages.PromptLogin;
        return new AuthRedirect(mode.BuildAuthorizeUrl(discovery, state, nonce, verifier, redirectUri, promptLogin));
    }

    /// <summary>
    /// Starts a link of a provider to the person of this web session (IDP-9). The answer holds the URL of the provider, and the SPA goes
    /// there. The attempt holds the ID of the session, and only a session that began at most 15 minutes ago starts one (IDP-10).
    /// </summary>
    private static async Task<IResult> StartLinkAsync(
        string id, HttpContext http, LoginModes modes, SessionStore store, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // The default scheme, so a provider token is a caller that this route knows and refuses, as a personal access token is.
        var session = await http.AuthenticateAsync();
        if (!session.Succeeded)
            return Results.Problem(detail: SignInToLinkMessage, statusCode: StatusCodes.Status401Unauthorized);

        // A link binds the attempt to the cookie of a browser, so only a web session starts one. A native app, a token, or an agent gets 403.
        if (session.Principal.FindFirst(AppSessionHandler.ClientKindClaim)?.Value != SessionKindText.Web)
            return Results.Problem(detail: WebSessionOnlyMessage, statusCode: StatusCodes.Status403Forbidden);

        var sessionId = session.Principal.FindFirst(AppSessionHandler.SessionIdClaim)!.Value;
        var began = await store.StartOfLiveWebSessionAsync(sessionId, cancellationToken);
        if (began is not { } start || timeProvider.GetUtcNow() - start > SessionPolicy.LinkSessionAge)
            return Results.Problem(detail: SignInAgainToLinkMessage, statusCode: StatusCodes.Status403Forbidden, type: SignInAgainToLinkType);

        if (modes.FindOidc(id) is not { } mode)
            return Results.Problem(detail: NoSuchProviderMessage, statusCode: StatusCodes.Status404NotFound);

        var redirectUri = AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback;
        if (redirectUri.Length > LoginAttempt.RedirectUriMaxLength)
            return Results.Problem(detail: NoSuchProviderMessage, statusCode: StatusCodes.Status400BadRequest);

        if (await mode.DiscoverAsync(cancellationToken) is not { } discovery)
            return Results.Problem(detail: AuthPages.ProviderDown(mode.DisplayName), statusCode: StatusCodes.Status503ServiceUnavailable);

        // With no prompt=login, the provider can answer with whoever is signed in there now, and the link binds that account (IDP-9).
        if (!discovery.AcceptsPromptLogin)
            return Results.Problem(detail: NoPromptLoginMessage(mode.DisplayName), statusCode: StatusCodes.Status409Conflict);

        var state = Secrets.New();
        var nonce = Secrets.New();
        var verifier = Secrets.New();
        var binding = Secrets.New();
        var stateHash = TokenHash.Of(state);
        var expiresAt = timeProvider.GetUtcNow() + SessionPolicy.AttemptLifetime;
        await store.AddAttemptAsync(new LoginAttempt
        {
            StateHash = stateHash.Hex,
            BindingHash = TokenHash.Of(binding).Hex,
            ModeId = mode.Id,
            Nonce = nonce,
            PkceVerifier = verifier,
            RedirectUri = redirectUri,
            ClientKind = SessionKind.Web,
            ReturnPath = SettingsPath,
            LinkSessionId = sessionId,
            ExpiresAt = expiresAt,
        }, cancellationToken);

        BindingCookie.Write(http.Response, http.Request, stateHash, binding, expiresAt);
        // The person signs in at the provider as the account they mean to link, and not as whoever is signed in there now.
        return Results.Json(new LinkAnswer(mode.BuildAuthorizeUrl(discovery, state, nonce, verifier, redirectUri, promptLogin: true)));
    }

    public sealed record LinkAnswer(string Url)
    {
        public override string ToString() => "[link answer]";
    }

    public const string SettingsPath = "/settings";
    public const string LinkResultQuery = "link";
    public const string SignInToLinkMessage = "Sign in to link a provider.";
    public const string WebSessionOnlyMessage = "Link a provider from Recall in a web browser.";
    public const string SignInAgainToLinkMessage = "Sign in again, then link the provider within 15 minutes.";

    /// <summary>The problem type of the IDP-10 refusal, so the page offers a new sign-in for it and for no other 403.</summary>
    public const string SignInAgainToLinkType = "urn:recall:problem:sign-in-again-to-link";

    public const string NoSuchProviderMessage = "This server has no such sign-in method.";

    public static string NoPromptLoginMessage(string provider)
        => $"Recall cannot link {provider}, because it does not let Recall ask you to sign in again there.";

    // The values of the query that the settings page reads when a link comes back.
    public const string Linked = "linked";
    public const string LinkedToAnother = "conflict";
    public const string LinkRefused = "refused";
    public const string LinkFailed = "failed";

    /// <summary>The query value that marks the second load of a link callback, the one that the page of Recall starts.</summary>
    private const string SameSiteAgain = "again";

    private static async Task<IResult> CallbackAsync(
        HttpContext http, LoginModes modes, SessionStore store, Provisioner provisioner, LoginCompletion completion, LinkExternalLogin linker,
        CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        if (OneValue(query, "state") is not { } state)
            return AuthPages.Refused();

        var stateHash = TokenHash.Of(state);
        var attempt = await store.FindAttemptAsync(stateHash, cancellationToken);
        if (attempt is null)
            return AuthPages.Refused();

        // A link that comes back in another browser adds nothing, and the person learns why in settings (IDP-9).
        if (!BindingCookie.Matches(http.Request, stateHash, attempt))
            return attempt.LinkSessionId is null ? AuthPages.Refused() : SettingsWith(LinkRefused);

        // The session cookie is Strict, so a browser does not send it on the redirect from the provider. A load that the page of Recall
        // starts is same-site, so the second load carries it, and the attempt is still unused.
        if (attempt.LinkSessionId is not null && SessionCookie.Read(http.Request) is null && !query.ContainsKey(SameSiteAgain))
            return AuthPages.ContinueHere(http.Request.Path + http.Request.QueryString.Add(SameSiteAgain, "1"));

        if (!await store.ConsumeAttemptAsync(attempt, cancellationToken))
            return AuthPages.Refused();

        BindingCookie.Clear(http.Response, http.Request, stateHash);

        var callback = new OidcCallback(OneValue(query, "code"), OneValue(query, "iss"), OneValue(query, "error"));
        if (attempt.LinkSessionId is { } linkSessionId)
            return await FinishLinkAsync(http, modes, store, linker, attempt, linkSessionId, callback, cancellationToken);

        var redeemed = modes.FindOidc(attempt.ModeId) is { } mode
            ? await mode.RedeemAsync(attempt, callback, cancellationToken)
            : RedeemResult.Failed(LoginFailure.InvalidResponse);
        var outcome = redeemed.Identity is { } identity
            ? await provisioner.ResolveAsync(identity, cancellationToken)
            : new ProvisionOutcome.Failed(redeemed.Failure!.Value);
        return await completion.CompleteAsync(LoginClient.FromAttempt(attempt), outcome, attempt.ModeId, http, cancellationToken);
    }

    /// <summary>
    /// The callback of a link (IDP-9, IDP-11). The session of the request must be the session that started the link, so a link that a
    /// stranger finishes in another browser, or after the session ended, adds nothing. It starts no session, and goes to settings.
    /// </summary>
    private static async Task<IResult> FinishLinkAsync(
        HttpContext http, LoginModes modes, SessionStore store, LinkExternalLogin linker, LoginAttempt attempt, string linkSessionId,
        OidcCallback callback, CancellationToken cancellationToken)
    {
        var session = SessionCookie.Read(http.Request) is { } cookie
            ? await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken)
            : null;
        if (session is null || session.SessionId != linkSessionId)
        {
            Telemetry.AddEvent([("app.link.result", LinkRefused)], "auth.link.refused");
            return SettingsWith(LinkRefused);
        }

        var redeemed = modes.FindOidc(attempt.ModeId) is { } mode
            ? await mode.RedeemAsync(attempt, callback, cancellationToken)
            : RedeemResult.Failed(LoginFailure.InvalidResponse);
        if (redeemed.Identity is not { } identity)
            return SettingsWith(LinkFailed);

        // The cookie of a GET with no X-CSRF authenticates no one, so the request has no caller yet. The checks above prove the session,
        // and this makes it the caller, so the link sees the tenant of the person and names them as the actor of its audit entry.
        http.User = AppSessionHandler.PrincipalOf(session);
        var result = await linker.LinkAsync(session.UserId, identity, cancellationToken) switch
        {
            LinkOutcome.Linked or LinkOutcome.AlreadyLinked => Linked,
            LinkOutcome.LinkedToAnotherUser => LinkedToAnother,
            _ => LinkRefused,
        };
        Telemetry.AddEvent([("app.link.result", result)], "auth.link.finished");
        return SettingsWith(result);
    }

    private static AuthRedirect SettingsWith(string result) => new($"{SettingsPath}?{LinkResultQuery}={result}");

    private static async Task<IResult> ConfirmAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        var form = await AuthForm.ReadAsync(http.Request, cancellationToken);
        if (form is null || AuthForm.OneValue(form, "code") is not { } code)
            return AuthPages.Refused();

        var found = await store.FindAppCodeAsync(TokenHash.Of(code), cancellationToken);
        if (found is null || !NativeRedirect.TryParse(found.RedirectUri, out var redirect))
            return AuthPages.Refused();

        return new AuthRedirect(redirect.WithQuery(("code", code), ("state", found.ClientState ?? "")));
    }

    private static IResult ProviderDown(HttpRequest request, string displayName)
    {
        var acceptsHtml = request.GetTypedHeaders().Accept
            .Any(accept => HtmlTypes.Contains(accept.MediaType.ToString(), StringComparer.OrdinalIgnoreCase));
        return acceptsHtml
            // The same start, so that a native app keeps its flow and a web login keeps its return path.
            ? AuthPages.ProviderIsDown(displayName, request.Path + request.QueryString)
            : Results.Json(new { error = "idp_unreachable", message = AuthPages.ProviderDown(displayName) }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static string? OneValue(IQueryCollection query, string name)
        => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
}
