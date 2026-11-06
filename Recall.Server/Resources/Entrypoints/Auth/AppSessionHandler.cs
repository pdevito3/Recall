namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;

/// <summary>A cookie needs <c>X-CSRF: 1</c>, and a cross-site page cannot send that header.</summary>
public sealed class AppSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AppSession";
    public const string AppTokenPrefix = "rcl_";
    public const string CsrfHeader = "X-CSRF";
    public const string SessionIdClaim = "sid";
    public const string ClientKindClaim = "client_kind";

    public static bool HasCsrfHeader(HttpRequest request) => request.Headers[CsrfHeader] == "1";

    public static string? CredentialOf(string authorization)
    {
        const string scheme = "Bearer ";
        if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        return authorization[scheme.Length..].Trim() is { Length: > 0 } token ? token : null;
    }

    public static ClaimsPrincipal PrincipalOf(SessionPrincipal session)
    {
        Claim[] claims =
        [
            new("sub", session.UserId),
            new("client_id", FirstPartyClientOptions.AppClientId),
            new(SessionIdClaim, session.SessionId),
            new(ClientKindClaim, SessionKindText.Of(session.Kind)),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "role"));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var credential = CredentialOf(Request.Headers.Authorization.ToString());
        if (credential is not null)
        {
            return credential.StartsWith(AppTokenPrefix, StringComparison.Ordinal)
                ? await ResultOfAsync(credential, SessionKind.Native)
                : AuthenticateResult.NoResult();
        }

        if (SessionCookie.Read(Request) is not { } cookie)
            return AuthenticateResult.NoResult();

        return HasCsrfHeader(Request)
            ? await ResultOfAsync(cookie, SessionKind.Web)
            : AuthenticateResult.Fail("A session cookie needs the X-CSRF header.");
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private async Task<AuthenticateResult> ResultOfAsync(string value, SessionKind kind)
    {
        var session = await store.FindByAccessAsync(TokenHash.Of(value), kind, Context.RequestAborted);
        if (session is null)
            return AuthenticateResult.Fail("The credential is not a live session.");

        var ticket = new AuthenticationTicket(
            PrincipalOf(session), new AuthenticationProperties { ExpiresUtc = session.AccessExpiresAt }, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
