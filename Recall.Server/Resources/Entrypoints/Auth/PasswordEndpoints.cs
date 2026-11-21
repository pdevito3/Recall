namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Globalization;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

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
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.PasswordSetup, SetupAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(AuthRoutes.PasswordChoose, ChooseAsync)
            .RequireRateLimiting(AuthRegistration.StartLimiter).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>
    /// Writes the attempt of one form and its binding cookie, and gives the state that the form posts back. A choose attempt
    /// holds the user ID (PWD-16). The login and the setup give null.
    /// </summary>
    public static async Task<string> StartFormAsync(
        HttpContext http, SessionStore store, TimeProvider timeProvider, LoginClient client, string? userId, CancellationToken cancellationToken)
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
            UserId = userId,
            ExpiresAt = expiresAt,
        }, cancellationToken);
        BindingCookie.Write(http.Response, http.Request, stateHash, binding, expiresAt);
        return state;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(http, store, isChoose: false, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var (attempt, form) = consumed;

        var client = LoginClient.FromAttempt(attempt);
        var typedName = AuthForm.OneValue(form, "loginName");
        var outcome = await passwords.SignInAsync(typedName, AuthForm.OneValue(form, "password"), http.Connection.RemoteIpAddress, cancellationToken);
        switch (outcome)
        {
            case PasswordOutcome.SignedIn signedIn:
                return await completion.CompleteAsync(client, signedIn.User, PasswordLoginMode.ModeId, http, cancellationToken);
            case PasswordOutcome.MustChoose choose:
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, choose.UserId, cancellationToken), client, null);
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return await LoginPageAsync(http, modes, store, timeProvider, client, typedName, wait.Message, StatusCodes.Status429TooManyRequests, cancellationToken);
            case PasswordOutcome.Refused refused:
                return await LoginPageAsync(http, modes, store, timeProvider, client, typedName, refused.Message, refused.Status, cancellationToken);
            default:
                return AuthPages.Refused();
        }
    }

    private static async Task<IResult> SetupPageAsync(HttpContext http, SessionStore store, IServiceScopeFactory scopes, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (await SetupCodeAtBoot.AnyLiveTenantAsync(scopes, cancellationToken))
            return AuthPages.AlreadySetUp();

        return AuthPages.Setup(await StartFormAsync(http, store, timeProvider, new LoginClient.Web("/"), null, cancellationToken), null, null, null);
    }

    private static async Task<IResult> SetupAsync(
        HttpContext http, SessionStore store, PasswordLogin passwords, LoginCompletion completion, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
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
                return await completion.CompleteAsync(LoginClient.FromAttempt(attempt), signedIn.User, PasswordLoginMode.ModeId, http, cancellationToken);
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
        HttpContext http, LoginModes modes, SessionStore store, PasswordLogin passwords, LoginCompletion completion, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(http, store, isChoose: true, cancellationToken) is not { } consumed)
            return AuthPages.Refused();

        var (attempt, form) = consumed;

        var client = LoginClient.FromAttempt(attempt);
        var outcome = await passwords.ChooseAsync(attempt.UserId!, AuthForm.OneValue(form, "password"), cancellationToken);
        switch (outcome)
        {
            case PasswordOutcome.SignedIn signedIn:
                return await completion.CompleteAsync(client, signedIn.User, PasswordLoginMode.ModeId, http, cancellationToken);
            case PasswordOutcome.Wait wait:
                SetRetryAfter(http, wait.Left);
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, attempt.UserId, cancellationToken), client, wait.Message,
                    StatusCodes.Status429TooManyRequests);
            // An expired temporary password goes back to the login page, which says so.
            case PasswordOutcome.Refused { Status: StatusCodes.Status401Unauthorized } expired:
                return await LoginPageAsync(http, modes, store, timeProvider, client, null, expired.Message, expired.Status, cancellationToken);
            case PasswordOutcome.Refused refused:
                return AuthPages.ChoosePassword(await StartFormAsync(http, store, timeProvider, client, attempt.UserId, cancellationToken), client, refused.Message,
                    refused.Status);
            default:
                return AuthPages.Refused();
        }
    }

    /// <summary>The login page again, with a new attempt, the typed login name, and the message of the failed post.</summary>
    private static async Task<IResult> LoginPageAsync(
        HttpContext http, LoginModes modes, SessionStore store, TimeProvider timeProvider, LoginClient client, string? typedName, string error, int status,
        CancellationToken cancellationToken)
    {
        var listed = await modes.ListAsync(cancellationToken);
        var states = await Task.WhenAll(listed.Select(mode => mode.ProbeAsync(cancellationToken)));
        var chooser = listed.Zip(states, (mode, state) => new AuthPages.ChooserMode(mode.Id, mode.DisplayName, state)).ToList();
        var form = new AuthPages.PasswordForm(await StartFormAsync(http, store, timeProvider, client, null, cancellationToken), typedName, error);
        return AuthPages.Chooser(chooser, client, signedOut: false, message: null, providerSession: null, passwordForm: form, status: status);
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
