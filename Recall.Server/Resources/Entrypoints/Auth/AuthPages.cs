namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Recall.Server.Auth;

public static class AuthPages
{
#pragma warning disable FSTMSG001 // An operator reads this one, and it must name the settings to set.
    public const string NoMethodMessage =
        "No sign-in method is configured. Set Auth__Providers__0__Issuer, ClientId, and ClientSecret, then restart.";
#pragma warning restore FSTMSG001

    private const string Style =
        "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;font:16px/1.5 system-ui,sans-serif;color-scheme:light dark}"
        + "main{max-width:28rem;padding:2rem}h1{font-size:1.5rem;margin:0 0 1rem}p{margin:0 0 1rem}code{overflow-wrap:anywhere}"
        + "ul{list-style:none;margin:1rem 0 0;padding:0}li{margin:.5rem 0}"
        + "a.button,button{display:block;box-sizing:border-box;width:100%;padding:.75rem 1rem;border:1px solid currentColor;border-radius:.5rem;background:none;color:inherit;font:inherit;text-align:center;text-decoration:none;cursor:pointer}";

    private static readonly string StyleHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Style)));

    // A form that redirects to a custom scheme fails under a form-action limit, so only the page with that form leaves it out.
    private static readonly string BasePolicy = $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; frame-ancestors 'none'";
    private static readonly string StrictPolicy = BasePolicy + "; form-action 'self'";

    public sealed record ChooserMode(string Id, string DisplayName, LoginModeState State);

    public static IResult NoMethod(string callbackUrl) => Page(
        StatusCodes.Status503ServiceUnavailable,
        "Sign-in is not set up",
        $"<p>{E(NoMethodMessage)}</p><p>Register this callback URL at the provider: <code>{E(callbackUrl)}</code></p>");

    public static IResult Chooser(IReadOnlyList<ChooserMode> modes, string returnPath, bool signedOut, string? message)
    {
        var items = modes.Select(mode => mode.State == LoginModeState.Ready
            ? $"<li><a class=\"button\" href=\"{E(StartUrl(mode.Id, returnPath))}\">Sign in with {E(mode.DisplayName)}</a></li>"
            : $"<li>{E(ProviderDown(mode.DisplayName))}</li>");
        var heading = signedOut ? "You are signed out" : "Sign in to Recall";
        var note = message is null ? "" : $"<p>{E(message)}</p>";
        return Page(StatusCodes.Status200OK, heading, $"{note}<ul>{string.Concat(items)}</ul>");
    }

    public static IResult ProviderIsDown(string displayName) => Page(
        StatusCodes.Status503ServiceUnavailable, "Sign-in is not available", $"<p>{E(ProviderDown(displayName))}</p>" + TryAgain);

    public static IResult SignupClosed() => Page(
        StatusCodes.Status403Forbidden,
        "This server does not accept new people",
        "<p>Your sign-in worked, but this Recall server has no account for you. Ask its owner to let you in.</p>");

    public static IResult AccessDenied() => Page(
        StatusCodes.Status403Forbidden, "Sign-in did not finish", "<p>The provider did not let this sign-in continue.</p>" + TryAgain);

    public static IResult InvalidResponse() => Page(
        StatusCodes.Status400BadRequest, "Sign-in failed", "<p>The provider sent an answer that this server cannot accept.</p>" + TryAgain);

    public static IResult Refused() => Page(
        StatusCodes.Status400BadRequest, "This sign-in is not valid", "<p>This sign-in link is used, too old, or from another browser.</p>" + TryAgain);

    public static IResult BadRequest() => Page(
        StatusCodes.Status400BadRequest, "This request is not valid", "<p>The sign-in request has a value that this server cannot accept.</p>");

    public static IResult NotFound() => Page(
        StatusCodes.Status404NotFound, "This sign-in method does not exist", "<p>This server has no such sign-in method.</p>" + TryAgain);

    /// <summary>The step of RFC 8252 section 8.6. An app that starts this flow on a silent provider session gets nothing until the person taps Continue.</summary>
    public static IResult ConfirmNative(string displayName, string code) => Page(
        StatusCodes.Status200OK,
        "Sign in the Recall app?",
        $"<p>Sign in the Recall app on this device as {E(displayName)}?</p>"
            + "<p>If you did not start this from the app, close this page.</p>"
            + $"<form method=\"post\" action=\"{AuthRoutes.Confirm}\"><input type=\"hidden\" name=\"code\" value=\"{E(code)}\"><button type=\"submit\">Continue</button></form>",
        BasePolicy);

    public static string ProviderDown(string displayName) => $"{displayName} is not available. Existing sessions keep working.";

    private const string TryAgain = "<ul><li><a class=\"button\" href=\"" + AuthRoutes.Login + "\">Try again</a></li></ul>";

    private static string StartUrl(string id, string returnPath)
        => $"/auth/oidc/{Uri.EscapeDataString(id)}/start?client=web&returnUrl={Uri.EscapeDataString(returnPath)}";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static PageResult Page(int status, string heading, string body, string? policy = null)
        => new(status, policy ?? StrictPolicy,
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(heading)} - Recall</title><style>{Style}</style></head><body><main><h1>{E(heading)}</h1>{body}</main></body></html>");

    private sealed class PageResult(int status, string policy, string html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers.ContentSecurityPolicy = policy;
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            return response.WriteAsync(html, httpContext.RequestAborted);
        }
    }
}
