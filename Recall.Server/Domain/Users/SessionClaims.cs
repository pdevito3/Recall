namespace Recall.Server.Domain.Users;

using System.Security.Claims;
using Auth.Entities;
using Resources.Entrypoints.Auth;

/// <summary>The kind of app session that a principal holds, as <see cref="AppSessionHandler"/> writes it.</summary>
public static class SessionClaims
{
    /// <summary>
    /// True for a web session of the app. Only the <c>AppSession</c> scheme makes a session principal, so a provider token
    /// with the same claims is not a web session.
    /// </summary>
    public static bool IsWebSession(ClaimsPrincipal? principal)
        => principal?.Identity is ClaimsIdentity { AuthenticationType: AppSessionHandler.SchemeName } identity
           && identity.FindFirst(AppSessionHandler.ClientKindClaim)?.Value == SessionKindText.Web
           && identity.FindFirst("client_id")?.Value == FirstPartyClientOptions.AppClientId;
}
