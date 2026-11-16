namespace Recall.Server.Domain.Users;

using System.Security.Claims;
using Resources.Entrypoints.Auth;

/// <summary>
/// The claims of a personal access token, the one codec that writes and reads them: <c>client_id</c> and <c>sid</c> are
/// the token ID, <c>client_kind</c> is <c>pat</c>, and <c>pat_grant</c> is <see cref="TokenGrant.Encode"/>.
/// </summary>
public sealed record PatClaims(string TokenId, TokenGrant Grant)
{
    public const string IdPrefix = "pat";
    public const string ClientKind = "pat";
    public const string GrantClaim = "pat_grant";

    /// <summary>True for an ID with the prefix of a token. No other client may take such an ID.</summary>
    public static bool IsTokenId(string? clientId)
        => clientId?.StartsWith(IdPrefix + "_", StringComparison.Ordinal) == true;

    public ClaimsPrincipal PrincipalOf(string subject)
    {
        Claim[] claims =
        [
            new("sub", subject),
            new("client_id", TokenId),
            new(AppSessionHandler.SessionIdClaim, TokenId),
            new(AppSessionHandler.ClientKindClaim, ClientKind),
            new(GrantClaim, Grant.Encode()),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AppSessionHandler.SchemeName, "name", "role"));
    }

    /// <summary>
    /// The token of the principal, or null. Only the <c>AppSession</c> scheme makes a token principal, so a provider
    /// token with the same claims is not a token.
    /// </summary>
    public static PatClaims? Read(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity { AuthenticationType: AppSessionHandler.SchemeName } identity
            || identity.FindFirst(AppSessionHandler.ClientKindClaim)?.Value != ClientKind
            || identity.FindFirst("client_id")?.Value is not { } tokenId
            || !IsTokenId(tokenId)
            || identity.FindFirst(AppSessionHandler.SessionIdClaim)?.Value != tokenId
            || identity.FindFirst(GrantClaim)?.Value is not { } grant)
            return null;

        return new PatClaims(tokenId, TokenGrant.Decode(grant));
    }
}
