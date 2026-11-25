namespace Recall.FunctionalTests.TestUtilities;

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Recall.Server.Resources.Entrypoints.Auth;

/// <summary>
/// The test server signs in with a fake bearer of its own scheme. A token that has <see cref="Marker"/> becomes a
/// principal of the <c>AppSession</c> scheme, as <see cref="AppSessionHandler"/> makes one, so a test can be a
/// personal access token before the Server issues one.
/// </summary>
public sealed class AppSessionTestPrincipal : IClaimsTransformation
{
    public const string Marker = "test_app_session";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        => Task.FromResult(principal.HasClaim(c => c.Type == Marker)
            ? new ClaimsPrincipal(new ClaimsIdentity(principal.Claims.Where(c => c.Type != Marker).Select(AsIssued), AppSessionHandler.SchemeName, "name", "role"))
            : principal);

    // The fake bearer maps sub to the name identifier type, and AppSessionHandler issues sub, which the page user check reads
    private static Claim AsIssued(Claim claim) => claim.Type == ClaimTypes.NameIdentifier ? new Claim("sub", claim.Value) : claim;
}
