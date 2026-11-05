namespace Recall.Server.Resources.Entrypoints.Auth;

using Recall.Server.Auth;

public sealed class LoginCompletion(SessionStore store, LoginModes modes)
{
    public Task<IResult> CompleteAsync(LoginClient client, ProvisionOutcome outcome, string modeId, HttpContext http, CancellationToken cancellationToken)
        => (client, outcome) switch
        {
            (LoginClient.Web web, ProvisionOutcome.Provisioned user) => StartWebAsync(web, user, modeId, http, cancellationToken),
            (LoginClient.Native native, ProvisionOutcome.Provisioned user) => OfferNativeAsync(native, user, modeId, cancellationToken),
            (LoginClient.Native native, ProvisionOutcome.SignupClosed) => Task.FromResult<IResult>(NativeError(native, "signup_closed")),
            (LoginClient.Native native, ProvisionOutcome.Failed failed) => Task.FromResult<IResult>(NativeError(native, ErrorCodeOf(failed.Failure))),
            (LoginClient.Web web, ProvisionOutcome.SignupClosed) => Task.FromResult(AuthPages.SignupClosed(modes.All, web.ReturnPath)),
            (_, ProvisionOutcome.Failed failed) => Task.FromResult(WebFailure(failed.Failure, modeId)),
            _ => throw new InvalidOperationException("A login outcome has no answer."),
        };

    private async Task<IResult> StartWebAsync(LoginClient.Web web, ProvisionOutcome.Provisioned user, string modeId, HttpContext http, CancellationToken cancellationToken)
    {
        var session = await store.StartWebAsync(user.UserId, modeId, cancellationToken);
        SessionCookie.Write(http.Response, http.Request, session.CookieValue, session.ExpiresAt);
        return new AuthRedirect(web.ReturnPath);
    }

    private async Task<IResult> OfferNativeAsync(LoginClient.Native native, ProvisionOutcome.Provisioned user, string modeId, CancellationToken cancellationToken)
    {
        var code = await store.AddAppCodeAsync(user.UserId, modeId, native.Redirect.Value, native.Challenge, native.ClientState, cancellationToken);
        return AuthPages.ConfirmNative(user.DisplayName, code);
    }

    private static AuthRedirect NativeError(LoginClient.Native native, string error)
        => new(native.Redirect.WithQuery(("error", error), ("state", native.ClientState)));

    private IResult WebFailure(LoginFailure failure, string modeId) => failure switch
    {
        LoginFailure.AccessDenied => AuthPages.AccessDenied(),
        LoginFailure.IdpUnreachable => AuthPages.ProviderIsDown(modes.FindOidc(modeId)?.DisplayName ?? modeId),
        _ => AuthPages.InvalidResponse(),
    };

    private static string ErrorCodeOf(LoginFailure failure) => failure switch
    {
        LoginFailure.AccessDenied => "access_denied",
        LoginFailure.IdpUnreachable => "idp_unreachable",
        _ => "invalid_response",
    };
}
