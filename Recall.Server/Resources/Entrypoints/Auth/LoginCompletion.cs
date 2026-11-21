namespace Recall.Server.Resources.Entrypoints.Auth;

using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

public sealed class LoginCompletion(SessionStore store, LoginModes modes)
{
    /// <summary>A password sign-in passes the guard of its credential, and an OIDC login passes none.</summary>
    /// <exception cref="Exceptions.StaleVersionException">The credential of <paramref name="guard"/> changed after the sign-in, and no session or code started.</exception>
    public Task<IResult> CompleteAsync(
        LoginClient client, ProvisionOutcome outcome, string modeId, HttpContext http, CancellationToken cancellationToken, CredentialGuard? guard = null)
        => (client, outcome) switch
        {
            (LoginClient.Web web, ProvisionOutcome.Provisioned user) => StartWebAsync(web, user, modeId, http, guard, cancellationToken),
            (LoginClient.Native native, ProvisionOutcome.Provisioned user) => OfferNativeAsync(native, user, modeId, guard, cancellationToken),
            (LoginClient.Native native, ProvisionOutcome.SignupClosed) => Task.FromResult<IResult>(NativeError(native, "signup_closed")),
            (LoginClient.Native native, ProvisionOutcome.Failed failed) => Task.FromResult<IResult>(NativeError(native, ErrorCodeOf(failed.Failure))),
            (LoginClient.Web web, ProvisionOutcome.SignupClosed) => Task.FromResult(AuthPages.SignupClosed(modes.Oidc, web.ReturnPath)),
            (_, ProvisionOutcome.Failed failed) => Task.FromResult(WebFailure(failed.Failure, modeId)),
            _ => throw new InvalidOperationException("A login outcome has no answer."),
        };

    private async Task<IResult> StartWebAsync(
        LoginClient.Web web, ProvisionOutcome.Provisioned user, string modeId, HttpContext http, CredentialGuard? guard, CancellationToken cancellationToken)
    {
        var session = await store.StartWebAsync(user.UserId, modeId, cancellationToken, guard);
        SessionCookie.Write(http.Response, http.Request, session.CookieValue, session.ExpiresAt);
        SessionCookie.ClearSignedOutMark(http.Response, http.Request);
        return new AuthRedirect(web.ReturnPath);
    }

    private async Task<IResult> OfferNativeAsync(
        LoginClient.Native native, ProvisionOutcome.Provisioned user, string modeId, CredentialGuard? guard, CancellationToken cancellationToken)
    {
        var code = await store.AddAppCodeAsync(user.UserId, modeId, native.Redirect.Value, native.Challenge, native.ClientState, cancellationToken, guard);
        // A typed password is the click that RFC 8252 section 8.6 asks for, so it needs no confirm page (PWD-7).
        return modeId == PasswordLoginMode.ModeId
            ? new AuthRedirect(native.Redirect.WithQuery(("code", code), ("state", native.ClientState)))
            : AuthPages.ConfirmNative(user.DisplayName, code);
    }

    private static AuthRedirect NativeError(LoginClient.Native native, string error)
        => new(native.Redirect.WithQuery(("error", error), ("state", native.ClientState)));

    private IResult WebFailure(LoginFailure failure, string modeId) => failure switch
    {
        LoginFailure.AccessDenied => AuthPages.AccessDenied(),
        LoginFailure.IdpUnreachable => AuthPages.ProviderIsDown(modes.FindOidc(modeId)?.DisplayName ?? modeId, AuthRoutes.Login),
        _ => AuthPages.InvalidResponse(),
    };

    private static string ErrorCodeOf(LoginFailure failure) => failure switch
    {
        LoginFailure.AccessDenied => "access_denied",
        LoginFailure.IdpUnreachable => "idp_unreachable",
        _ => "invalid_response",
    };
}
