namespace Recall.Server.Domain.Users;

using System.Security.Claims;
using Auth.Entities;
using Resources.Entrypoints.Auth;

public static class SessionClaims
{
    public static bool IsWebSession(ClaimsPrincipal? principal)
        => principal?.Identity is ClaimsIdentity { AuthenticationType: AppSessionHandler.SchemeName } identity
           && identity.FindFirst(AppSessionHandler.ClientKindClaim)?.Value == SessionKindText.Web
           && identity.FindFirst("client_id")?.Value == FirstPartyClientOptions.AppClientId;
}
