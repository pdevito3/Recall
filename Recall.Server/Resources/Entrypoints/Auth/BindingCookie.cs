namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Security.Cryptography;
using System.Text;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

/// <summary>
/// Ties an attempt to the browser that started it. There is one cookie for each attempt, so two restored tabs do not
/// overwrite each other. On TLS the <c>__Host-</c> name keeps a sibling host from planting it.
/// </summary>
public static class BindingCookie
{
    private const string SecurePrefix = "__Host-recall_login_";
    private const string PlainPrefix = "recall_login_";
    private const string PlainPath = "/auth/oidc";
    private const int NameHexLength = 8;

    public static string NameFor(HttpRequest request, TokenHash state)
        => (request.IsHttps ? SecurePrefix : PlainPrefix) + state.Hex[..NameHexLength];

    public static void Write(HttpResponse response, HttpRequest request, TokenHash state, string value, DateTimeOffset expires)
        => response.Cookies.Append(NameFor(request, state), value, Options(request, expires));

    public static bool Matches(HttpRequest request, TokenHash state, LoginAttempt attempt)
    {
        var value = request.Cookies[NameFor(request, state)];
        return value is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(TokenHash.Of(value).Hex), Encoding.ASCII.GetBytes(attempt.BindingHash));
    }

    public static void Clear(HttpResponse response, HttpRequest request, TokenHash state)
        => response.Cookies.Delete(NameFor(request, state), Options(request, null));

    private static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        // A provider redirect is a cross-site navigation, and Strict would hide the cookie from the callback
        SameSite = SameSiteMode.Lax,
        Path = request.IsHttps ? "/" : PlainPath,
        Expires = expires,
    };
}
