namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints.Jobs;
using Serilog;

/// <summary>
/// A cookie needs <c>X-CSRF: 1</c>, and a cross-site page cannot send that header. A browser that opens
/// <see cref="JobDashboard.Path"/> cannot send it either, so <see cref="JobDashboard"/> guards its own writes.
/// </summary>
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

    internal static readonly TimeSpan UseMarkWait = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// The person of a link after a temporary password, who has no session yet (IDP-20). It names the user and the app client, so a save
    /// names that person as its actor. It has no session and no client kind, so no route that needs a session takes it.
    /// </summary>
    public static ClaimsPrincipal PrincipalOfLinkingUser(string userId)
        => new(new ClaimsIdentity([new Claim("sub", userId), new Claim("client_id", FirstPartyClientOptions.AppClientId)], SchemeName, "name", "role"));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var credential = CredentialOf(Request.Headers.Authorization.ToString());
        if (credential is not null)
        {
            if (credential.StartsWith(NewPersonalAccessToken.Prefix, StringComparison.Ordinal))
                return await ResultOfPersonalAccessTokenAsync(credential);

            return credential.StartsWith(AppTokenPrefix, StringComparison.Ordinal)
                ? await ResultOfAsync(credential, SessionKind.Native)
                : AuthenticateResult.NoResult();
        }

        if (SessionCookie.Read(Request) is not { } cookie)
            return AuthenticateResult.NoResult();

        if (JobMcp.Serves(Request.Path))
            return AuthenticateResult.Fail("The job tools take a bearer credential, not a session cookie.");

        return HasCsrfHeader(Request) || JobDashboard.Serves(Request.Path)
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

    private async Task<AuthenticateResult> ResultOfPersonalAccessTokenAsync(string value)
    {
        var token = await store.FindPersonalAccessTokenAsync(TokenHash.Of(value), Context.RequestAborted);
        if (token is null)
            return AuthenticateResult.Fail("The credential is not a live personal access token.");

        if (token.UseMarkDue)
        {
            var scopes = Context.RequestServices.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(() => NoteUseAsync(scopes, token.TokenId), CancellationToken.None);
        }

        var ticket = new AuthenticationTicket(
            new PatClaims(token.TokenId, token.Grant).PrincipalOf(token.UserId),
            new AuthenticationProperties { ExpiresUtc = token.AccessExpiresAt },
            SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    // No request waits for the mark. A mark in OnCompleted held the next request on a keep-alive connection for the full
    // wait in the writer line. A mark that the line holds past UseMarkWait is skipped, and a later request marks the use.
    private static async Task NoteUseAsync(IServiceScopeFactory scopes, string tokenId)
    {
        var mark = MarkInItsOwnScopeAsync(scopes, tokenId);
        try
        {
            await mark.WaitAsync(UseMarkWait);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                          || DomainError.From(exception).Kind == DomainErrorKind.DatabaseBusy)
        {
            Telemetry.AddEvent([("exception.type", exception.GetType().Name)], "personal_access_token.use_mark_skipped");
            if (exception is TimeoutException)
                _ = WarnOfALateFailureAsync(mark, tokenId);
        }
        catch (Exception exception)
        {
            Warn(exception, tokenId);
        }
    }

    private static async Task MarkInItsOwnScopeAsync(IServiceScopeFactory scopes, string tokenId)
    {
        using var activity = Telemetry.Trace([("app.personal_access_token_id", tokenId)], "personal_access_token.note_use");
        using var limit = new CancellationTokenSource(UseMarkWait);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SessionStore>().NotePersonalAccessTokenUseAsync(tokenId, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.MarkFailed(activity, exception);
            throw;
        }
    }

    private static async Task WarnOfALateFailureAsync(Task mark, string tokenId)
    {
        try
        {
            await mark;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(exception, tokenId);
        }
    }

    private static void Warn(Exception exception, string tokenId)
        => Log.ForContext<AppSessionHandler>().Here().Warning(exception,
            "The mark of the last use of personal access token {TokenId} failed. A write that does not end can mean a disk that hangs",
            tokenId);
}
