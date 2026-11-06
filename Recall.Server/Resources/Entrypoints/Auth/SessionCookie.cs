namespace Recall.Server.Resources.Entrypoints.Auth;

/// <summary>On https the reader sees only the <c>__Host-</c> name, so a plain-http neighbor cannot plant one.</summary>
public static class SessionCookie
{
    public sealed record PendingProviderLogout(string ModeId, string State);

    public const string SecureName = "__Host-recall";
    public const string PlainName = "recall";
    public const string SecureSignedOutName = "__Host-recall_signed_out";
    public const string PlainSignedOutName = "recall_signed_out";

    private const string ProviderSessionFinished = "1";
    private const string ProviderSessionOpenPrefix = "open:";

    private static readonly TimeSpan FinishedMarkLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnfinishedProviderLogoutMarkLifetime = TimeSpan.FromDays(30);

    public static string NameFor(HttpRequest request) => request.IsHttps ? SecureName : PlainName;

    public static string? Read(HttpRequest request) => request.Cookies[NameFor(request)];

    public static void Write(HttpResponse response, HttpRequest request, string value, DateTimeOffset expires)
        => response.Cookies.Append(NameFor(request), value, Options(request, expires));

    public static void Clear(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(NameFor(request), Options(request, null));

    public static void MarkSignedOut(HttpResponse response, HttpRequest request, PendingProviderLogout? pending)
    {
        var options = Options(request, null);
        options.MaxAge = pending is null ? FinishedMarkLifetime : UnfinishedProviderLogoutMarkLifetime;
        // A provider returns with a cross-site GET, so this mark is Lax where the session cookie is Strict.
        options.SameSite = SameSiteMode.Lax;
        var value = pending is null ? ProviderSessionFinished : $"{ProviderSessionOpenPrefix}{pending.State}:{pending.ModeId}";
        response.Cookies.Append(SignedOutNameFor(request), value, options);
    }

    public static void ClearSignedOutMark(HttpResponse response, HttpRequest request)
        => response.Cookies.Delete(SignedOutNameFor(request), Options(request, null));

    public static bool IsMarkedSignedOut(HttpRequest request) => request.Cookies.ContainsKey(SignedOutNameFor(request));

    public static PendingProviderLogout? PendingProviderLogoutOf(HttpRequest request)
        => request.Cookies[SignedOutNameFor(request)] is { } value
           && value.StartsWith(ProviderSessionOpenPrefix, StringComparison.Ordinal)
           && value[ProviderSessionOpenPrefix.Length..].Split(':', 2) is [var state, var modeId]
            ? new PendingProviderLogout(modeId, state)
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
