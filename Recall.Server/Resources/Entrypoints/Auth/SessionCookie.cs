namespace Recall.Server.Resources.Entrypoints.Auth;

/// <summary>On https the reader sees only the <c>__Host-</c> name, so a plain-http neighbor cannot plant one.</summary>
public static class SessionCookie
{
    public const string SecureName = "__Host-recall";
    public const string PlainName = "recall";
    public const string SecureSignedOutName = "__Host-recall_signed_out";
    public const string PlainSignedOutName = "recall_signed_out";

    private const string ProviderSessionFinished = "1";
    private const string ProviderSessionOpenPrefix = "open:";

    // Long enough for a person to answer the confirm page of a provider.
    private static readonly TimeSpan SignedOutMarkLifetime = TimeSpan.FromMinutes(10);

    public static string NameFor(HttpRequest request) => request.IsHttps ? SecureName : PlainName;

    public static string? Read(HttpRequest request) => request.Cookies[NameFor(request)];

    public static void Write(HttpResponse response, HttpRequest request, string value, DateTimeOffset expires)
        => response.Cookies.Append(NameFor(request), value, Options(request, expires));

    public static void Clear(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(NameFor(request), Options(request, null));

    /// <summary>
    /// The proof that the sign-out of this browser ended its session, and the login mode whose provider session can still be open. A link
    /// from any site can open the signed-out page. A provider also returns there with a cross-site GET, so this mark is Lax where the
    /// session cookie is Strict. A new session deletes the mark, so a link cannot show the signed-out page next to a live session.
    /// </summary>
    public static void MarkSignedOut(HttpResponse response, HttpRequest request, string? openProviderSessionMode)
    {
        var options = Options(request, null);
        options.MaxAge = SignedOutMarkLifetime;
        options.SameSite = SameSiteMode.Lax;
        var value = openProviderSessionMode is null ? ProviderSessionFinished : ProviderSessionOpenPrefix + openProviderSessionMode;
        response.Cookies.Append(SignedOutNameFor(request), value, options);
    }

    public static void ClearSignedOutMark(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(SignedOutNameFor(request), Options(request, null));

    public static bool IsMarkedSignedOut(HttpRequest request) => request.Cookies.ContainsKey(SignedOutNameFor(request));

    public static string? OpenProviderSessionModeOf(HttpRequest request)
        => request.Cookies[SignedOutNameFor(request)] is { } value && value.StartsWith(ProviderSessionOpenPrefix, StringComparison.Ordinal)
            ? value[ProviderSessionOpenPrefix.Length..]
            : null;

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
