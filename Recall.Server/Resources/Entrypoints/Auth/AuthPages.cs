namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Recall.Server.Auth;

public static class AuthPages
{
#pragma warning disable FSTMSG001 // An operator reads this one, and it must name the settings to set.
    public const string NoMethodMessage =
        "No sign-in method is configured. Set Auth__Providers__0__Issuer, Auth__Providers__0__ClientId, and Auth__Providers__0__ClientSecret, then restart. See docs/authentication.md.";
#pragma warning restore FSTMSG001

    // The colors are the theme tokens of frontend/src/index.css. A page has no script, so it follows the system theme and cannot read a choice that the app stored.
    private const string Style =
        ":root{color-scheme:light dark;--background:oklch(1 0 0);--foreground:oklch(0.1363 0.0364 259.2010);--primary:oklch(0.5461 0.2152 262.8809);--primary-foreground:oklch(1 0 0);--border:oklch(0.9288 0.0126 255.5078)}"
        + "@media (prefers-color-scheme:dark){:root{--background:oklch(0.1363 0.0364 259.2010);--foreground:oklch(0.9842 0.0034 247.8575);--primary:oklch(0.6231 0.1880 259.8145);--border:oklch(0.2795 0.0368 260.0310)}}"
        + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:var(--background);color:var(--foreground);font:16px/1.5 system-ui,sans-serif}"
        + "main{box-sizing:border-box;width:100%;max-width:28rem;padding:2rem;overflow-wrap:anywhere}@media (max-width:24rem){main{padding:1rem}}h1{font-size:1.5rem;margin:0 0 1rem}p{margin:0 0 1rem}code{font-size:.875rem}"
        + "ul{list-style:none;margin:1rem 0 0;padding:0}li{margin:.5rem 0}li.note{margin:1rem 0}ul+p{margin-top:1rem}form{margin:1rem 0 0}"
        + "a{color:var(--primary)}a.button,button{display:block;box-sizing:border-box;width:100%;padding:.75rem 1rem;border:0;border-radius:.5rem;background:var(--primary);color:var(--primary-foreground);font:inherit;text-align:center;text-decoration:none;cursor:pointer}";

    private static readonly string StyleHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Style)));

    // A form that redirects to a custom scheme fails under a form-action limit, so only the page with that form leaves it out.
    private static readonly string BasePolicy = $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; frame-ancestors 'none'";
    private static readonly string StrictPolicy = BasePolicy + "; form-action 'self'";

    private static readonly string[] NoMethodNames =
        ["Auth__Providers__0__Issuer", "Auth__Providers__0__ClientId", "Auth__Providers__0__ClientSecret", "docs/authentication.md"];

    public const string PromptLogin = "login";

    public const string AnotherPersonMessage = "Another person signed in on this browser, so the page that you had open closed. Any words that this browser could not send wait on this device. They go to your journal when you sign in here again.";

    public sealed record ChooserMode(string Id, string DisplayName, LoginModeState State);

    public sealed record OpenProviderSession(string DisplayName, LoginModeState State, string? LogoutUrl);

    public static IResult NoMethod(string callbackUrl, string returnPath) => Page(
        StatusCodes.Status503ServiceUnavailable,
        "Sign-in is not set up",
        $"<p>{NoMethodNames.Aggregate(E(NoMethodMessage), (html, name) => html.Replace(name, $"<code>{name}</code>", StringComparison.Ordinal))}</p>"
            + $"<p>Register this callback URL at the provider:</p><p><code>{BreakableUrl(callbackUrl)}</code></p>"
            + TryAgainTo(returnPath));

    public static IResult Chooser(IReadOnlyList<ChooserMode> modes, string returnPath, bool signedOut, string? message, OpenProviderSession? providerSession, bool asksTheProviderAgain = false)
    {
        // The session at the provider belongs to the person who signed in last, and a plain start would sign that person in again.
        var promptQuery = asksTheProviderAgain ? "&prompt=" + PromptLogin : "";
        var items = modes.Select(mode => mode.State == LoginModeState.Ready
            ? $"<li><a class=\"button\" href=\"{E(StartUrl(mode.Id, returnPath) + promptQuery)}\">Sign in with {E(mode.DisplayName)}</a></li>"
            : $"<li class=\"note\">{E(NotAvailable(mode.DisplayName))}</li>");
        var note = message is null ? "" : $"<p>{E(message)}</p>";
        var sessionNote = providerSession is null ? "" : OpenSessionNote(providerSession);
        var noneReady = modes.All(mode => mode.State != LoginModeState.Ready);
        var heading = signedOut ? "You are signed out" : noneReady ? "Sign-in is not available" : "Sign in to Recall";
        return noneReady
            ? Page(
                StatusCodes.Status503ServiceUnavailable,
                heading,
                $"{note}<ul>{string.Concat(items)}</ul>{sessionNote}<p>Wait a few minutes, then try again.</p>" + TryAgainTo(returnPath))
            : Page(StatusCodes.Status200OK, heading, $"{note}<ul>{string.Concat(items)}</ul>{sessionNote}");
    }

    private static string OpenSessionNote(OpenProviderSession session)
    {
        var name = E(session.DisplayName);
        return session switch
        {
            { LogoutUrl: { } url } =>
                $"<p>Recall cannot tell if your session at {name} ended. If you did not sign out there, anyone on this device can sign in to Recall as you with no password.</p><p><a href=\"{E(url)}\">Sign out of {name}</a></p>",
            { State: LoginModeState.Unreachable } =>
                $"<p>{name} did not answer, so your session there can still be open. Until it ends, anyone on this device can sign in to Recall as you with no password.</p>",
            _ =>
                $"<p>Your session at {name} stays open, because {name} does not let Recall end it. Anyone on this device can sign in to Recall as you with no password. To stop this, sign out on the site of {name}, or delete the cookies of this browser.</p>",
        };
    }

    public static IResult ProviderIsDown(string displayName, string tryAgainUrl) => Page(
        StatusCodes.Status503ServiceUnavailable,
        "Sign-in is not available",
        $"<p>{E(NotAvailable(displayName))} Wait a few minutes, then try again.</p>" + Action("Try again", tryAgainUrl));

    public static IResult SignupClosed(IReadOnlyList<ILoginMode> modes, string returnPath)
    {
        var items = modes.Select(mode =>
        {
            var label = modes.Count == 1 ? "Use another account" : $"Use another account with {mode.DisplayName}";
            return $"<li><a class=\"button\" href=\"{E(StartUrl(mode.Id, returnPath) + "&prompt=" + PromptLogin)}\">{E(label)}</a></li>";
        });
        return Page(
            StatusCodes.Status403Forbidden,
            "This server does not accept new people",
            "<p>Your sign-in worked, but this Recall server has no account for you. Use the account that you use for Recall, or ask the owner of this server to let you in.</p>"
                + $"<ul>{string.Concat(items)}</ul>"
                + "<p>If the provider signs in the same account again, sign out at the provider first.</p>");
    }

    public static IResult TooManyStarts(int seconds, string startUrl) => Page(
        StatusCodes.Status429TooManyRequests,
        "Too many sign-in attempts",
        $"<p>This server got too many sign-in attempts from your network. Wait {seconds} seconds, then try again.</p>"
            + Action("Try again", startUrl));

    public static IResult AccessDenied() => Page(
        StatusCodes.Status403Forbidden, "Sign-in did not finish", "<p>The provider did not let this sign-in continue.</p>" + TryAgain);

    public static IResult InvalidResponse() => Page(
        StatusCodes.Status400BadRequest, "Sign-in failed", "<p>The provider sent an answer that this server cannot accept.</p>" + TryAgain);

    public static IResult Refused() => Page(
        StatusCodes.Status400BadRequest, "This sign-in is not valid", "<p>This sign-in link is used, too old, or from another browser.</p>" + TryAgain);

    public static IResult BadRequest() => Page(
        StatusCodes.Status400BadRequest, "This request is not valid", "<p>The sign-in request has a value that this server cannot accept.</p>" + TryAgain);

    public static IResult NotFound() => Page(
        StatusCodes.Status404NotFound, "This sign-in method does not exist", "<p>This server has no such sign-in method.</p>" + TryAgain);

    /// <summary>The step of RFC 8252 section 8.6. An app that starts this flow on a silent provider session gets nothing until the person taps Continue.</summary>
    public static IResult ConfirmNative(string displayName, string code) => Page(
        StatusCodes.Status200OK,
        "Sign in to the Recall app?",
        $"<p>You are signing in on this device as {E(displayName)}.</p>"
            + "<p>If you did not start this from the app, close this page.</p>"
            + $"<form method=\"post\" action=\"{AuthRoutes.Confirm}\"><input type=\"hidden\" name=\"code\" value=\"{E(code)}\"><button type=\"submit\">Continue</button></form>",
        BasePolicy);

    public static string ProviderDown(string displayName) => $"{displayName} is not available. Existing sessions keep working.";

    private static string NotAvailable(string displayName) => $"{displayName} is not available.";

    private static readonly string TryAgain = Action("Try again", AuthRoutes.Login);

    private static string TryAgainTo(string returnPath) => Action("Try again", $"{AuthRoutes.Login}?returnUrl={Uri.EscapeDataString(returnPath)}");

    private static string Action(string label, string url) => $"<ul><li><a class=\"button\" href=\"{E(url)}\">{E(label)}</a></li></ul>";

    private static string StartUrl(string id, string returnPath)
        => $"/auth/oidc/{Uri.EscapeDataString(id)}/start?client=web&returnUrl={Uri.EscapeDataString(returnPath)}";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A person copies this URL, so it can wrap before a slash of its path. The browser can also wrap at a hyphen of the host, and anywhere in a part that is wider than the page.</summary>
    private static string BreakableUrl(string url)
    {
        var path = url.IndexOf('/', url.IndexOf("://", StringComparison.Ordinal) + 3);
        return E(url[..path]) + E(url[path..]).Replace("/", "<wbr>/", StringComparison.Ordinal);
    }

    private static PageResult Page(int status, string heading, string body, string? policy = null)
        => new(status, policy ?? StrictPolicy,
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(heading.Contains("Recall", StringComparison.Ordinal) ? heading : $"{heading} - Recall")}</title><style>{Style}</style></head><body><main><h1>{E(heading)}</h1>{body}</main></body></html>");

    private sealed class PageResult(int status, string policy, string html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = policy;
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            return response.WriteAsync(html, httpContext.RequestAborted);
        }
    }
}
