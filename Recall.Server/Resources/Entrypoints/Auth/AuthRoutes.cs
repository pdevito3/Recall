namespace Recall.Server.Resources.Entrypoints.Auth;

public static class AuthRoutes
{
    public const string Status = "/auth/status";
    public const string Login = "/auth/login";
    public const string Start = "/auth/oidc/{id}/start";
    public const string Callback = "/auth/oidc/callback";
    public const string Confirm = "/auth/oidc/confirm";
    public const string Token = "/auth/token";
    public const string Logout = "/auth/logout";
    public const string RevokeOthers = "/auth/sessions/revoke-others";
    public const string Me = "/auth/me";
    public const string BffLogin = "/bff/login";
    public const string BffUser = "/bff/user";
    public const string BffLogout = "/bff/logout";

    /// <summary>The origin of this request. The scheme comes from the connection or from <c>X-Forwarded-Proto</c>, and the host only from <c>Host</c>.</summary>
    public static string OriginOf(HttpRequest request) => $"{request.Scheme}://{request.Host}";
}
