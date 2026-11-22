namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Globalization;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Exceptions;

/// <summary>
/// The forms of password login. Each form post needs the live attempt that the page of the form wrote and the binding
/// cookie of that attempt, and it consumes the attempt, so a form posts once (PWD-5). A page that shows the form again
/// writes a new attempt.
/// </summary>
public static class PasswordEndpoints
{
    /// <summary>The form body limit of PWD-8. A password of 256 code points takes at most 3 KiB in a form.</summary>
    public const long FormMaxBytes = 8 * 1024;

    public static void MapPasswordEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(AuthRoutes.PasswordLogin, LoginAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.PasswordSetup, SetupPageAsync)
            .RequireRateLimiting(AuthRegistration.PageLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.PasswordSetup, SetupAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.PasswordChoose, ChooseAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.PasswordClaimOwner, ClaimOwnerPageAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.PasswordClaimOwner, ClaimOwnerAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        // The session cookie with X-CSRF: 1, or the bearer token of a native session, as for revoke-others. A personal access token gets 403.
        endpoints.MapPost(AuthRoutes.PasswordChange, ChangeAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).RequireAuthorization(AuthEndpoints.InteractiveSessionOnly).ExcludeFromDescription();
    }

    /// <summary>
    /// The login page offers <c>Set a new Owner password</c> only while a fresh <c>claim-owner</c> file and a live tenant exist (PWD-19).
    /// With no such file, the page is the same as before the file, so it tells a visitor nothing.
    /// </summary>
    public static async Task<bool> OffersOwnerClaimAsync(OwnerClaim ownerClaim, IServiceScopeFactory scopes, CancellationToken cancellationToken)
        => ownerClaim.IsFresh() && await SetupCodeAtBoot.AnyLiveTenantAsync(scopes, cancellationToken);

    /// <summary>
    /// Writes the attempt of one form and its binding cookie, and gives the state that the form posts back. A choose attempt
    /// holds the user ID and the time of the temporary password (PWD-16). The login and the setup give null.
    /// </summary>
    public static async Task<string> StartFormAsync(
        HttpContext http, SessionStore store, TimeProvider timeProvider, LoginClient client, PasswordOutcome.MustChoose? choose, CancellationToken cancellationToken)
    {
        var state = Secrets.New();
        var binding = Secrets.New();
        var stateHash = TokenHash.Of(state);
        var expiresAt = timeProvider.GetUtcNow() + SessionPolicy.AttemptLifetime;
        var native = client as LoginClient.Native;
        await store.AddAttemptAsync(new LoginAttempt
        {
            StateHash = stateHash.Hex,
            BindingHash = TokenHash.Of(binding).Hex,
            ModeId = PasswordLoginMode.ModeId,
            Nonce = "",
            PkceVerifier = "",
            RedirectUri = "",
            ClientKind = native is null ? SessionKind.Web : SessionKind.Native,
            ReturnPath = (client as LoginClient.Web)?.ReturnPath,
            NativeRedirect = native?.Redirect.Value,
            NativeChallenge = native?.Challenge,
            NativeState = native?.ClientState,
            UserId = choose?.UserId,
            CredentialChangedAt = choose?.ChangedAt,
            ExpiresAt = expiresAt,
        }, cancellationToken);
        BindingCookie.Write(http.Response, http.Request, stateHash, binding, expiresAt);
        return state;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, OwnerClaim ownerClaim,
        IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(http, store, isChoose: false, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var (attempt, form) = consumed;

        var client = LoginClient.FromAttempt(attempt);
        var typedName = AuthForm.OneValue(form, "loginName");
        // While password login is Off, only a temporary password signs in, and only to link a provider (IDP-15, IDP-20).
        var outcome = await passwords.SignInAsync(
            typedName, AuthForm.OneValue(form, "password"), http.Connection.RemoteIpAddress, temporaryOnly: !modes.PasswordsAreOn, cancellationToken);
        switch (outcome)
        {
            // Password login turned Off during the hash, so the right password gets the answer of PWD-6 and starts nothing (IDP-15).
            case PasswordOutcome.SignedIn when !modes.PasswordsAreOn:
                return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, PasswordLogin.WrongMessage,
                    StatusCodes.Status401Unauthorized, cancellationToken);
            case PasswordOutcome.SignedIn signedIn:
                return await CompleteAsync(completion, passwords, client, signedIn, http, cancellationToken);
            // While password login is Off, a temporary password only links a provider, and the session starts after the link (IDP-20).
            case PasswordOutcome.MustChoose choose when !modes.PasswordsAreOn:
                return await StartTemporaryLinkAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, choose, cancellationToken);
            case PasswordOutcome.MustChoose choose:
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, choose, cancellationToken), client, null);
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, wait.Message, StatusCodes.Status429TooManyRequests, cancellationToken);
            case PasswordOutcome.Refused refused:
                return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, refused.Message, refused.Status, cancellationToken);
            default:
                return AuthPages.Refused();
        }
    }

    /// <summary>
    /// Starts the login at a Usable provider with <c>prompt=login</c> after a right temporary password (IDP-20). Each attempt holds the user
    /// and the time of the temporary password, as a choose attempt does, and the client of the form. With several providers, the page
    /// links to each, and the attempts that the person does not use expire. A provider that does not let Recall ask for a new sign-in is
    /// left out, as for a link from settings (IDP-9). With no Usable provider, the form says so (IDP-15).
    /// </summary>
    private static async Task<IResult> StartTemporaryLinkAsync(
        HttpContext http, LoginModes modes, SessionStore store, OwnerClaim ownerClaim, IServiceScopeFactory scopes, TimeProvider timeProvider,
        LoginClient client, string? typedName, PasswordOutcome.MustChoose choose, CancellationToken cancellationToken)
    {
        var redirectUri = AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback;
        if (modes.Oidc.Count == 0 || redirectUri.Length > LoginAttempt.RedirectUriMaxLength)
            return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, AuthPages.NoLoginMethodMessage,
                StatusCodes.Status503ServiceUnavailable, cancellationToken);

        var native = client as LoginClient.Native;
        var starts = new List<(string DisplayName, string Url)>();
        foreach (var mode in modes.Oidc)
        {
            if (await mode.DiscoverAsync(cancellationToken) is not { AcceptsPromptLogin: true } discovery)
                continue;

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
                ClientKind = native is null ? SessionKind.Web : SessionKind.Native,
                ReturnPath = (client as LoginClient.Web)?.ReturnPath,
                NativeRedirect = native?.Redirect.Value,
                NativeChallenge = native?.Challenge,
                NativeState = native?.ClientState,
                UserId = choose.UserId,
                CredentialChangedAt = choose.ChangedAt,
                ExpiresAt = expiresAt,
            }, cancellationToken);
            BindingCookie.Write(http.Response, http.Request, stateHash, binding, expiresAt);
            starts.Add((mode.DisplayName, mode.BuildAuthorizeUrl(discovery, state, nonce, verifier, redirectUri, promptLogin: true)));
        }

        return starts switch
        {
            [] => await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, typedName, AuthPages.NoProviderAnsweredMessage,
                StatusCodes.Status503ServiceUnavailable, cancellationToken),
            [var only] => new AuthRedirect(only.Url),
            _ => AuthPages.ChooseProviderToLink(starts, client),
        };
    }

    private static async Task<IResult> SetupPageAsync(
        HttpContext http, LoginModes modes, SessionStore store, IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // With a provider, the first OIDC login makes the Owner (PWD-4), so the install has no setup.
        if (modes.Oidc.Count > 0)
            return Results.NotFound();
        if (await SetupCodeAtBoot.AnyLiveTenantAsync(scopes, cancellationToken))
            return AuthPages.AlreadySetUp();

        return AuthPages.Setup(await StartFormAsync(http, store, timeProvider, new LoginClient.Web("/"), null, cancellationToken), null, null, null);
    }

    private static async Task<IResult> SetupAsync(
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (modes.Oidc.Count > 0)
            return Results.NotFound();
        if (await ConsumeAsync(http, store, isChoose: false, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var (attempt, form) = consumed;

        var name = AuthForm.OneValue(form, "name");
        var loginName = AuthForm.OneValue(form, "loginName");
        var outcome = await passwords.SetupAsync(
            AuthForm.OneValue(form, "code"), name, loginName, AuthForm.OneValue(form, "password"), cancellationToken);
        switch (outcome)
        {
            case PasswordOutcome.SignedIn signedIn:
                return await CompleteAsync(completion, passwords, LoginClient.FromAttempt(attempt), signedIn, http, cancellationToken);
            case PasswordOutcome.AlreadySetUp:
                return AuthPages.AlreadySetUp();
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return AuthPages.Setup(await StartFormAsync(http, store, timeProvider, new LoginClient.Web("/"), null, cancellationToken), name, loginName, wait.Message,
                    StatusCodes.Status429TooManyRequests);
            case PasswordOutcome.Refused refused:
                return AuthPages.Setup(await StartFormAsync(http, store, timeProvider, new LoginClient.Web("/"), null, cancellationToken), name, loginName, refused.Message,
                    refused.Status);
            default:
                return AuthPages.Refused();
        }
    }

    private static async Task<IResult> ChooseAsync(
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, OwnerClaim ownerClaim,
        IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // While password login is Off, no one picks a password (IDP-15).
        if (!modes.PasswordsAreOn)
            return Results.NotFound();
        if (await ConsumeAsync(http, store, isChoose: true, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var (attempt, form) = consumed;

        var client = LoginClient.FromAttempt(attempt);
        // An attempt from before the column has none, so it cannot prove that the temporary password is the same.
        if (attempt.CredentialChangedAt is not { } changedAt)
            return AuthPages.Refused();

        var choose = new PasswordOutcome.MustChoose(attempt.UserId!, changedAt);
        var outcome = await passwords.ChooseAsync(choose.UserId, choose.ChangedAt, AuthForm.OneValue(form, "password"), cancellationToken);
        switch (outcome)
        {
            // Password login turned Off during the choice, so no session starts (IDP-15). An Admin issues a new temporary password.
            case PasswordOutcome.SignedIn when !modes.PasswordsAreOn:
                return Results.NotFound();
            case PasswordOutcome.SignedIn signedIn:
                return await CompleteAsync(completion, passwords, client, signedIn, http, cancellationToken);
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, choose, cancellationToken), client, wait.Message,
                    StatusCodes.Status429TooManyRequests);
            // An expired temporary password goes back to the login page, which says so.
            case PasswordOutcome.Refused { Status: StatusCodes.Status401Unauthorized } expired:
                return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, null, expired.Message, expired.Status, cancellationToken);
            case PasswordOutcome.Refused refused:
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, choose, cancellationToken), client, refused.Message,
                    refused.Status);
            default:
                return AuthPages.Refused();
        }
    }

    /// <summary>
    /// Starts the session or the app code of a right password, in a save that checks the credential is as the sign-in saved
    /// it. An issue of a temporary password in between makes that save stale, so nothing starts and the post gets the
    /// Refused page. The failed save ends the context of the request, so no new form can be written.
    /// </summary>
    private static async Task<IResult> CompleteAsync(
        LoginCompletion completion, PasswordLogin passwords, LoginClient client, PasswordOutcome.SignedIn signedIn, HttpContext http,
        CancellationToken cancellationToken)
    {
        if (passwords.BeforeSessionWrite is { } beforeSessionWrite)
            await beforeSessionWrite(signedIn.User.UserId);
        try
        {
            return await completion.CompleteAsync(client, signedIn.User, PasswordLoginMode.ModeId, http, cancellationToken, signedIn.Guard);
        }
        catch (StaleVersionException)
        {
            return AuthPages.Refused();
        }
    }

    private static async Task<IResult> ClaimOwnerPageAsync(
        HttpContext http, SessionStore store, OwnerClaim ownerClaim, IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await OffersOwnerClaimAsync(ownerClaim, scopes, cancellationToken))
            return new AuthRedirect(AuthRoutes.Login);

        return AuthPages.ClaimOwner(await StartFormAsync(http, store, timeProvider, new LoginClient.Web("/"), null, cancellationToken), null, null);
    }

    private static async Task<IResult> ClaimOwnerAsync(
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, OwnerClaim ownerClaim,
        IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(http, store, isChoose: false, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var client = new LoginClient.Web("/");
        var loginName = AuthForm.OneValue(consumed.Form, "loginName");
        var outcome = await passwords.ClaimOwnerAsync(loginName, AuthForm.OneValue(consumed.Form, "password"), cancellationToken);
        switch (outcome)
        {
            // The session starts with the guard of the claimed credential, as a sign-in does, so an issue after the claim starts nothing.
            case PasswordOutcome.SignedIn signedIn:
                return await CompleteAsync(completion, passwords, client, signedIn, http, cancellationToken);
            case PasswordOutcome.NoClaim:
                return await LoginPageAsync(http, modes, store, ownerClaim, scopes, timeProvider, client, null, PasswordLogin.ClaimGoneMessage,
                    StatusCodes.Status409Conflict, cancellationToken);
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return AuthPages.ClaimOwner(await StartFormAsync(http, store, timeProvider, client, null, cancellationToken), loginName, wait.Message,
                    StatusCodes.Status429TooManyRequests);
            case PasswordOutcome.Refused refused:
                return AuthPages.ClaimOwner(await StartFormAsync(http, store, timeProvider, client, null, cancellationToken), loginName, refused.Message,
                    refused.Status);
            default:
                return AuthPages.Refused();
        }
    }

    /// <summary>
    /// The change of a password from the SPA or an app (PWD-17). A refusal is a problem whose <c>errors</c> names the field, so the
    /// form shows the message at that field.
    /// </summary>
    private static async Task<IResult> ChangeAsync(HttpContext http, LoginModes modes, PasswordLogin passwords, CancellationToken cancellationToken)
    {
        if (!modes.PasswordsAreOn)
            return Results.NotFound();

        var form = await AuthForm.ReadAsync(http.Request, cancellationToken, FormMaxBytes);
        if (form is null)
            return Results.Problem(detail: ChangeFormMessage, statusCode: StatusCodes.Status400BadRequest);

        var outcome = await passwords.ChangeAsync(
            http.User.FindFirst("sub")!.Value,
            http.User.FindFirst(AppSessionHandler.SessionIdClaim)!.Value,
            AuthForm.OneValue(form, "currentPassword"),
            AuthForm.OneValue(form, "newPassword"),
            http.Connection.RemoteIpAddress,
            cancellationToken);
        switch (outcome)
        {
            case PasswordOutcome.Changed:
                return Results.NoContent();
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return Results.Problem(detail: wait.Message, statusCode: StatusCodes.Status429TooManyRequests);
            case PasswordOutcome.Refused { Status: StatusCodes.Status403Forbidden } wrong:
                return FieldProblem(wrong, "currentPassword");
            case PasswordOutcome.Refused { Status: StatusCodes.Status422UnprocessableEntity } refused:
                return FieldProblem(refused, "newPassword");
            case PasswordOutcome.Refused other:
                return Results.Problem(detail: other.Message, statusCode: other.Status);
            default:
                throw new InvalidOperationException($"A password change cannot end as {outcome}.");
        }
    }

    private const string ChangeFormMessage = "Send the current password and the new password as a form.";

    private static IResult FieldProblem(PasswordOutcome.Refused refused, string field)
        => Results.Problem(
            detail: refused.Message,
            statusCode: refused.Status,
            extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [field] = [refused.Message] } });

    /// <summary>The login page again, with a new attempt, the typed login name, and the message of the failed post.</summary>
    private static async Task<IResult> LoginPageAsync(
        HttpContext http, LoginModes modes, SessionStore store, OwnerClaim ownerClaim, IServiceScopeFactory scopes, TimeProvider timeProvider, LoginClient client, string? typedName, string error, int status,
        CancellationToken cancellationToken)
    {
        var listed = await modes.ListAsync(cancellationToken);
        var states = await Task.WhenAll(listed.Select(mode => mode.ProbeAsync(cancellationToken)));
        var chooser = listed.Zip(states, (mode, state) => new AuthPages.ChooserMode(mode.Id, mode.DisplayName, state)).ToList();
        var form = new AuthPages.PasswordForm(await StartFormAsync(http, store, timeProvider, client, null, cancellationToken), typedName, error);
        return AuthPages.Chooser(chooser, client, signedOut: false, message: null, providerSession: null, passwordForm: form, status: status,
            offersOwnerClaim: await OffersOwnerClaimAsync(ownerClaim, scopes, cancellationToken));
    }

    private static async Task<(LoginAttempt Attempt, IFormCollection Form)?> ConsumeAsync(
        HttpContext http, SessionStore store, bool isChoose, CancellationToken cancellationToken)
    {
        var form = await AuthForm.ReadAsync(http.Request, cancellationToken, FormMaxBytes);
        if (form is null || AuthForm.OneValue(form, "state") is not { } state)
            return null;

        var stateHash = TokenHash.Of(state);
        var attempt = await store.FindAttemptAsync(stateHash, cancellationToken);
        if (attempt is null
            || attempt.ModeId != PasswordLoginMode.ModeId
            || attempt.UserId is not null != isChoose
            || !BindingCookie.Matches(http.Request, stateHash, attempt)
            || !await store.ConsumeAttemptAsync(attempt, cancellationToken))
            return null;

        BindingCookie.Clear(http.Response, http.Request, stateHash);
        return (attempt, form);
    }

    private static void SetRetryAfter(HttpContext http, TimeSpan left)
        => http.Response.Headers.RetryAfter = ((int)Math.Ceiling(left.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
