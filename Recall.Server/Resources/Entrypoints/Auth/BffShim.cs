namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users.Features;

public static class BffShim
{
    public const string LogoutUrlClaim = "bff:logout_url";
    public const string SessionExpiresInClaim = "bff:session_expires_in";

    private const string SignedOutPage = $"{AuthRoutes.Login}?signedOut=1";

    public sealed record ClaimRecord(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("value")] object Value);

    public static void MapBffShim(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthRoutes.BffLogin, Login).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.BffUser, UserAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(AuthRoutes.BffLogout, LogoutAsync).AllowAnonymous().ExcludeFromDescription();
    }

    private static AuthRedirect Login(HttpContext http)
    {
        var values = http.Request.Query["returnUrl"];
        var returnPath = LocalReturnPath.Of(values.Count == 1 ? values[0] : null);
        return new AuthRedirect($"{AuthRoutes.Login}?returnUrl={Uri.EscapeDataString(returnPath)}");
    }

    private static async Task<IResult> UserAsync(HttpContext http, ISender sender, SessionStore store, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!AppSessionHandler.HasCsrfHeader(http.Request))
            return Results.Unauthorized();

        var result = await http.AuthenticateAsync(AppSessionHandler.SchemeName);
        if (!result.Succeeded
            || result.Principal.FindFirst(AppSessionHandler.ClientKindClaim)?.Value != SessionKindText.Web)
            return Results.NoContent();

        http.User = result.Principal;
        var current = await sender.Send(new GetCurrentUser.Query(), cancellationToken);
        var sessionId = result.Principal.FindFirst(AppSessionHandler.SessionIdClaim)!.Value;
        await AuthEndpoints.SlideAsync(http, store, sessionId, cancellationToken);

        var claims = new List<ClaimRecord>
        {
            new("sub", current.Identifier),
            new(AppSessionHandler.SessionIdClaim, sessionId),
            new(LogoutUrlClaim, $"{AuthRoutes.BffLogout}?sid={Uri.EscapeDataString(sessionId)}"),
        };
        if (current.Name is { } name)
            claims.Add(new ClaimRecord("name", name));
        if (result.Properties.ExpiresUtc is { } expires)
            claims.Add(new ClaimRecord(SessionExpiresInClaim, Math.Round((expires - timeProvider.GetUtcNow()).TotalSeconds)));

        return Results.Json(claims);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionStore store, CancellationToken cancellationToken)
    {
        var sid = http.Request.Query["sid"].ToString();
        if (SessionCookie.Read(http.Request) is not { } cookie)
        {
            // SameSite=Strict holds the cookie back on a navigation from another site, so this request cannot end the session that it names.
            return sid.Length > 0 && await store.IsLiveWebSessionAsync(sid, cancellationToken)
                ? AuthPages.LogoutFromElsewhere()
                : new AuthRedirect(SignedOutPage);
        }

        if (await store.FindByAccessAsync(TokenHash.Of(cookie), SessionKind.Web, cancellationToken) is { } session)
        {
            if (sid != session.SessionId)
                return AuthPages.WrongLogoutLink();

            await store.RevokeAsync(session.SessionId, RevokeReason.Logout, cancellationToken);
        }

        SessionCookie.Clear(http.Response, http.Request);
        SessionCookie.MarkSignedOut(http.Response, http.Request);
        return new AuthRedirect(SignedOutPage);
    }
}
