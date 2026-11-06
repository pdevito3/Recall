namespace Recall.Server.Resources.Entrypoints.Auth;

/// <summary>On https the reader sees only the <c>__Host-</c> name, so a plain-http neighbor cannot plant one.</summary>
public static class SessionCookie
{
    public const string SecureName = "__Host-recall";
    public const string PlainName = "recall";
    public const string SecureSignedOutName = "__Host-recall_signed_out";
    public const string PlainSignedOutName = "recall_signed_out";

    private static readonly TimeSpan SignedOutMarkLifetime = TimeSpan.FromSeconds(60);

    public static string NameFor(HttpRequest request) => request.IsHttps ? SecureName : PlainName;

    public static string? Read(HttpRequest request) => request.Cookies[NameFor(request)];

    public static void Write(HttpResponse response, HttpRequest request, string value, DateTimeOffset expires)
        => response.Cookies.Append(NameFor(request), value, Options(request, expires));

    public static void Clear(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(NameFor(request), Options(request, null));

    /// <summary>
    /// The proof that the sign-out of this browser ended its session. A link from any site can open the signed-out page, and on
    /// such a navigation the page sees no session cookie, also when the session is alive. SameSite=Strict keeps this mark away from it too.
    /// </summary>
    public static void MarkSignedOut(HttpResponse response, HttpRequest request)
    {
        var options = Options(request, null);
        options.MaxAge = SignedOutMarkLifetime;
        response.Cookies.Append(SignedOutNameFor(request), "1", options);
    }

    public static bool IsMarkedSignedOut(HttpRequest request) => request.Cookies.ContainsKey(SignedOutNameFor(request));

    private static string SignedOutNameFor(HttpRequest request) => request.IsHttps ? SecureSignedOutName : PlainSignedOutName;

    private static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
    };
}
