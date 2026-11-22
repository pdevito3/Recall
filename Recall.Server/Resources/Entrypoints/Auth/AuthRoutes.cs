namespace Recall.Server.Resources.Entrypoints.Auth;

public static class AuthRoutes
{
    public const string Status = "/auth/status";
    public const string Login = "/auth/login";
    public const string Start = "/auth/oidc/{id}/start";
    public const string Link = "/auth/oidc/{id}/link";
    public const string Callback = "/auth/oidc/callback";
    public const string Confirm = "/auth/oidc/confirm";
    public const string Token = "/auth/token";
    public const string Logout = "/auth/logout";
    public const string SignedOut = "/auth/signed-out";
    public const string RevokeOthers = "/auth/sessions/revoke-others";
    public const string Me = "/auth/me";
    public const string PasswordLogin = "/auth/password/login";
    public const string PasswordSetup = "/auth/password/setup";
    public const string PasswordChoose = "/auth/password/choose";
    public const string PasswordChange = "/auth/password/change";
    public const string PasswordClaimOwner = "/auth/password/claim-owner";

    public static string OriginOf(HttpRequest request) => $"{request.Scheme}://{request.Host}";
}
