namespace Recall.Server.Resources.Entrypoints.Auth;

using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

public static class OidcEndpoints
{
    private static readonly string[] HtmlTypes = ["text/html"];

    public static void MapOidcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthRoutes.Start, StartAsync)
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
        return new AuthRedirect(mode.BuildAuthorizeUrl(discovery, state, nonce, verifier, redirectUri));
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext http, LoginModes modes, SessionStore store, Provisioner provisioner, LoginCompletion completion, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        if (OneValue(query, "state") is not { } state)
            return AuthPages.Refused();

        var stateHash = TokenHash.Of(state);
        var attempt = await store.FindAttemptAsync(stateHash, cancellationToken);
        if (attempt is null || !BindingCookie.Matches(http.Request, stateHash, attempt))
            return AuthPages.Refused();

        if (!await store.ConsumeAttemptAsync(attempt, cancellationToken))
            return AuthPages.Refused();

        BindingCookie.Clear(http.Response, http.Request, stateHash);

        var callback = new OidcCallback(OneValue(query, "code"), OneValue(query, "iss"), OneValue(query, "error"));
        var redeemed = modes.FindOidc(attempt.ModeId) is { } mode
            ? await mode.RedeemAsync(attempt, callback, cancellationToken)
            : RedeemResult.Failed(LoginFailure.InvalidResponse);
        var outcome = redeemed.Identity is { } identity
            ? await provisioner.ResolveAsync(identity, cancellationToken)
            : new ProvisionOutcome.Failed(redeemed.Failure!.Value);
        return await completion.CompleteAsync(LoginClient.FromAttempt(attempt), outcome, attempt.ModeId, http, cancellationToken);
    }

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
            ? AuthPages.ProviderIsDown(displayName)
            : Results.Json(new { error = "idp_unreachable", message = AuthPages.ProviderDown(displayName) }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static string? OneValue(IQueryCollection query, string name)
        => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
}
