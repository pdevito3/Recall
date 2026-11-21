namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Recall.Server.Auth;

public static class AuthPages
{
    private const string LightTokens = "--background:oklch(1 0 0);--foreground:oklch(0.1363 0.0364 259.2010);--primary:oklch(0.5461 0.2152 262.8809);--primary-foreground:oklch(1 0 0);--border:oklch(0.9288 0.0126 255.5078)";

    private const string DarkTokens = "--background:oklch(0.1363 0.0364 259.2010);--foreground:oklch(0.9842 0.0034 247.8575);--primary:oklch(0.6231 0.1880 259.8145);--primary-foreground:oklch(1 0 0);--border:oklch(0.2795 0.0368 260.0310)";

    /// <summary>The app writes this cookie, because a page here has no script and cannot read the choice that the app stored. With no choice, a page follows the system theme.</summary>
    public const string ThemeCookie = "ui-theme";

    // The colors are the theme tokens of frontend/src/index.css.
    private const string Style =
        ":root{color-scheme:light dark;" + LightTokens + "}"
        + "@media (prefers-color-scheme:dark){:root{" + DarkTokens + "}}"
        + "html.light{color-scheme:light;" + LightTokens + "}html.dark{color-scheme:dark;" + DarkTokens + "}"
        + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:var(--background);color:var(--foreground);font:16px/1.5 system-ui,sans-serif}"
        + "main{box-sizing:border-box;width:100%;max-width:28rem;padding:2rem;overflow-wrap:anywhere}@media (max-width:24rem){main{padding:1rem}}h1{font-size:1.5rem;margin:0 0 1rem}p{margin:0 0 1rem}code{font-size:.875rem}"
        + "ul{list-style:none;margin:1rem 0 0;padding:0}li{margin:.5rem 0}li.note{margin:1rem 0}ul+p{margin-top:1rem}form{margin:1rem 0 0}"
        + "label{display:block;margin:.75rem 0 .25rem}input{box-sizing:border-box;width:100%;min-height:2.75rem;padding:.5rem .75rem;border:1px solid var(--border);border-radius:.5rem;background:var(--background);color:var(--foreground);font:inherit}form button{margin-top:1rem}"
        + "a{color:var(--primary)}a.button,button{display:block;box-sizing:border-box;width:100%;min-height:2.75rem;padding:.75rem 1rem;border:0;border-radius:.5rem;background:var(--primary);color:var(--primary-foreground);font:inherit;text-align:center;text-decoration:none;cursor:pointer}";

    private static readonly string StyleHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Style)));

    // A form that redirects to a custom scheme fails under a form-action limit, so only the page with that form leaves it out.
    private static readonly string BasePolicy = $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; frame-ancestors 'none'";
    private static readonly string StrictPolicy = BasePolicy + "; form-action 'self'";

    public const string PromptLogin = "login";

    public const string AnotherPersonMessage = "Another person signed in on this browser, so the page that you had open closed. Any unsent words of that page wait on this device. They go to your journal when you sign in here again.";

    public const string AnotherPersonLostWordsMessage = "Another person signed in on this browser, so the page that you had open closed. That page held words that this browser had no room to keep, and they are gone. Any other unsent words of that page wait on this device, and they go to your journal when you sign in here again.";

    public sealed record ChooserMode(string Id, string DisplayName, LoginModeState State);

    public sealed record OpenProviderSession(string DisplayName, LoginModeState State, string? LogoutUrl);

    /// <summary>The password form of the login page. <paramref name="LoginName"/> is the name that the person typed, so a wrong password keeps it.</summary>
    public sealed record PasswordForm(string State, string? LoginName, string? Error);

    public static IResult Chooser(
        IReadOnlyList<ChooserMode> modes, LoginClient client, bool signedOut, string? message, OpenProviderSession? providerSession,
        bool asksTheProviderAgain = false, PasswordForm? passwordForm = null, int status = StatusCodes.Status200OK)
    {
        // The session at the provider belongs to the person who signed in last, and a plain start would sign that person in again.
        var promptQuery = asksTheProviderAgain ? "&prompt=" + PromptLogin : "";
        var items = modes.Where(mode => mode.Id != PasswordLoginMode.ModeId).Select(mode => mode.State == LoginModeState.Ready
            ? $"<li><a class=\"button\" href=\"{E(StartUrl(mode.Id, client) + promptQuery)}\">Sign in with {E(mode.DisplayName)}</a></li>"
            : $"<li class=\"note\">{E(NotAvailable(mode.DisplayName))}</li>").ToList();
        var list = items.Count == 0 ? "" : $"<ul>{string.Concat(items)}</ul>";
        var note = message is null ? "" : $"<p>{E(message)}</p>";
        var form = passwordForm is null ? "" : LoginForm(passwordForm);
        var sessionNote = providerSession is null ? "" : OpenSessionNote(providerSession);
        var noneReady = modes.All(mode => mode.State != LoginModeState.Ready);
        var heading = signedOut ? "You are signed out" : noneReady ? "Sign-in is not available" : "Sign in to Recall";
        return noneReady
            ? Page(
                StatusCodes.Status503ServiceUnavailable,
                heading,
                $"{note}{list}{sessionNote}<p>Wait a few minutes, then try again.</p>" + Action("Try again", LoginUrl(client)))
            : Page(status, heading, $"{note}{form}{list}{sessionNote}", PolicyFor(client));
    }

    public static IResult Setup(string state, string? name, string? loginName, string? error, int status = StatusCodes.Status200OK) => Page(
        status,
        "Set up Recall",
        "<p>You become the Owner of this Recall server. Find the setup code in the Server log, for example with <code>docker compose logs</code>.</p>"
            + Alert(error)
            + $"<form method=\"post\" action=\"{AuthRoutes.PasswordSetup}\">{Hidden(state)}"
            + "<label for=\"code\">Setup code</label><input id=\"code\" name=\"code\" autocomplete=\"off\" autocapitalize=\"characters\" spellcheck=\"false\" required>"
            + $"<label for=\"name\">Your name</label><input id=\"name\" name=\"name\" autocomplete=\"name\" value=\"{E(name ?? "")}\" required>"
            + $"<label for=\"loginName\">Login name</label><input id=\"loginName\" name=\"loginName\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" value=\"{E(loginName ?? "")}\" required>"
            + NewPasswordField()
            + "<button type=\"submit\">Set up and sign in</button></form>");

    public static IResult ChoosePassword(string state, LoginClient client, string? error, int status = StatusCodes.Status200OK) => Page(
        status,
        "Pick your password",
        "<p>Your temporary password worked. Pick the password that you use from now on.</p>"
            + Alert(error)
            + $"<form method=\"post\" action=\"{AuthRoutes.PasswordChoose}\">{Hidden(state)}"
            + NewPasswordField()
            + "<button type=\"submit\">Save and sign in</button></form>",
        PolicyFor(client));

    public static IResult AlreadySetUp() => Page(
        StatusCodes.Status409Conflict,
        "This Server is already set up",
        $"<p>{E(Recall.Server.Auth.PasswordLogin.AlreadySetUpMessage)}</p>" + Action("Sign in", AuthRoutes.Login));

    private static string LoginForm(PasswordForm form)
        => Alert(form.Error)
            + $"<form method=\"post\" action=\"{AuthRoutes.PasswordLogin}\">{Hidden(form.State)}"
            + $"<label for=\"loginName\">Login name</label><input id=\"loginName\" name=\"loginName\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" value=\"{E(form.LoginName ?? "")}\" required>"
            + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>"
            + "<button type=\"submit\">Sign in</button></form>";

    private static string NewPasswordField()
        => "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"new-password\" minlength=\"15\" required>"
            + "<p>Use at least 15 characters. A phrase of four words is long enough and easy to remember.</p>";

    private static string Hidden(string state) => $"<input type=\"hidden\" name=\"state\" value=\"{E(state)}\">";

    private static string Alert(string? error) => error is null ? "" : $"<p role=\"alert\">{E(error)}</p>";

    // The post of a native login ends in a redirect to the app, and a form-action limit would stop it.
    private static string PolicyFor(LoginClient client) => client is LoginClient.Native ? BasePolicy : StrictPolicy;

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
            return $"<li><a class=\"button\" href=\"{E(StartUrl(mode.Id, new LoginClient.Web(returnPath)) + "&prompt=" + PromptLogin)}\">{E(label)}</a></li>";
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

    public static IResult OwnerOnly(string returnPath) => Page(
        StatusCodes.Status403Forbidden,
        "Only the Owner can see the jobs",
        "<p>You are signed out, or this account is not the Owner.</p>"
            + Action("Sign in", $"{AuthRoutes.Login}?returnUrl={Uri.EscapeDataString(returnPath)}")
            + "<p><a href=\"/\">Go to Recall</a></p>");

    public static IResult JobDashboardFault(string tryAgainPath) => Page(
        StatusCodes.Status500InternalServerError,
        "The job pages cannot open",
        "<p>The server could not read the background jobs.</p>"
            + "<p>Open <code>/health</code>. If it says The background jobs stopped, do the steps in the section of that name in <code>docs/troubleshooting.md</code>. If it does not, try again in a minute.</p>"
            + Action("Try again", tryAgainPath)
            + "<p><a href=\"/\">Go to Recall</a></p>");

    public static string ProviderDown(string displayName) => $"{displayName} is not available. Existing sessions keep working.";

    private static string NotAvailable(string displayName) => $"{displayName} is not available.";

    private static readonly string TryAgain = Action("Try again", AuthRoutes.Login);

    private static string LoginUrl(LoginClient client) => client is LoginClient.Web web
        ? $"{AuthRoutes.Login}?returnUrl={Uri.EscapeDataString(web.ReturnPath)}"
        : $"{AuthRoutes.Login}?{LoginClient.QueryOf(client)}";

    private static string Action(string label, string url) => $"<ul><li><a class=\"button\" href=\"{E(url)}\">{E(label)}</a></li></ul>";

    public static string StartUrl(string id, LoginClient client)
        => $"/auth/oidc/{Uri.EscapeDataString(id)}/start?{LoginClient.QueryOf(client)}";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static PageResult Page(int status, string heading, string body, string? policy = null)
        => new(status, policy ?? StrictPolicy,
            themeClass => $"<!doctype html><html lang=\"en\"{themeClass}><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(heading.Contains("Recall", StringComparison.Ordinal) ? heading : $"{heading} - Recall")}</title><style>{Style}</style></head><body><main><h1>{E(heading)}</h1>{body}</main></body></html>");

    private sealed class PageResult(int status, string policy, Func<string, string> html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = policy;
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            var themeClass = httpContext.Request.Cookies[ThemeCookie] switch
            {
                "light" => " class=\"light\"",
                "dark" => " class=\"dark\"",
                _ => "",
            };
            return response.WriteAsync(html(themeClass), httpContext.RequestAborted);
        }
    }
}
