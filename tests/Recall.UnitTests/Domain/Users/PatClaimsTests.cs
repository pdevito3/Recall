namespace Recall.UnitTests.Domain.Users;

using System.Security.Claims;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class PatClaimsTests
{
    private const string TokenId = "pat_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f";
    private const string Subject = "usr_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f";

    private static readonly PatClaims Token = new(TokenId, new TokenGrant([Permission.ReadUsers()], JournalAccess.Read));

    [Test]
    public void the_claims_of_a_token_read_back_as_the_token()
    {
        // Arrange
        var principal = Token.PrincipalOf(Subject);

        // Act
        var read = PatClaims.Read(principal);

        // Assert
        read.ShouldBe(Token);
        principal.FindFirst("sub")!.Value.ShouldBe(Subject);
        principal.FindFirst("client_id")!.Value.ShouldBe(TokenId);
        principal.FindFirst(AppSessionHandler.SessionIdClaim)!.Value.ShouldBe(TokenId);
        principal.FindFirst(AppSessionHandler.ClientKindClaim)!.Value.ShouldBe("pat");
    }

    [Test]
    public void the_same_claims_from_another_scheme_are_not_a_token()
    {
        // Arrange
        var principal = new ClaimsPrincipal(new ClaimsIdentity(Token.PrincipalOf(Subject).Claims, "AuthenticationTypes.Federation"));

        // Act & Assert
        PatClaims.Read(principal).ShouldBeNull();
    }

    public static IEnumerable<Func<(string Case, Claim[] Claims)>> NotATokenCases()
    {
        Claim[] Without(string type) => Token.PrincipalOf(Subject).Claims.Where(c => c.Type != type).ToArray();
        Claim[] With(string type, string value) => [.. Without(type), new Claim(type, value)];

        yield return () => ("a web session", [new("sub", Subject), new("client_id", "recall"), new("sid", "ses_1"), new("client_kind", "web")]);
        yield return () => ("no client kind", Without(AppSessionHandler.ClientKindClaim));
        yield return () => ("no grant", Without(PatClaims.GrantClaim));
        yield return () => ("a client that is not a token ID", With("client_id", "recall"));
        yield return () => ("a session ID that is not the token ID", With(AppSessionHandler.SessionIdClaim, "pat_other"));
    }

    [Test]
    [MethodDataSource(nameof(NotATokenCases))]
    public void an_app_session_principal_without_each_claim_of_a_token_is_not_a_token(string @case, Claim[] claims)
    {
        // Arrange
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AppSessionHandler.SchemeName));

        // Act & Assert
        PatClaims.Read(principal).ShouldBeNull();
    }

    [Test]
    [Arguments("pat_0199f0c1", true)]
    [Arguments("pat_", true)]
    [Arguments("pat", false)]
    [Arguments("PAT_0199f0c1", false)]
    [Arguments("spat_0199f0c1", false)]
    [Arguments(null, false)]
    public void a_token_id_starts_with_pat_and_an_underscore(string? clientId, bool isTokenId)
    {
        // Act & Assert
        PatClaims.IsTokenId(clientId).ShouldBe(isTokenId);
    }
}
