namespace Recall.Server.Resources.Entrypoints.Auth;

/// <summary>The one place that knows the session cookie. On https the reader sees only the <c>__Host-</c> name, so a plain-http neighbor cannot plant one.</summary>
public static class SessionCookie
{
    public const string SecureName = "__Host-recall";
    public const string PlainName = "recall";

    public static string NameFor(HttpRequest request) => request.IsHttps ? SecureName : PlainName;

    public static string? Read(HttpRequest request) => request.Cookies[NameFor(request)];

    public static void Write(HttpResponse response, HttpRequest request, string value, DateTimeOffset expires)
        => response.Cookies.Append(NameFor(request), value, Options(request, expires));

    public static void Clear(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(NameFor(request), Options(request, null));

    private static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
    };
}
