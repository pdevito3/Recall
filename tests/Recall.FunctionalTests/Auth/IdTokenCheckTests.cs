namespace Recall.FunctionalTests.Auth;

using System.Net;
using Shouldly;

public class IdTokenCheckTests
{
    private static string NewSubject() => $"id-token-{Guid.NewGuid():N}";

    private static async Task ShouldBeRefusedAsync(StubLogin login)
    {
        using var browser = new AuthBrowser(AuthHosts.Open);
        var authorize = await browser.StartWebAsync();

        var response = await browser.FinishAtProviderAsync(authorize, login);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public Task an_id_token_for_another_audience_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenAudiences = ["another-client"] });

    [Test]
    public Task an_id_token_with_many_audiences_and_the_azp_of_another_client_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject())
        {
            IdTokenAudiences = [StubOidcProvider.ClientId, "another-client"],
            IdTokenAzp = "another-client",
        });

    [Test]
    public Task an_id_token_with_many_audiences_and_no_azp_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenAudiences = [StubOidcProvider.ClientId, "another-client"] });

    [Test]
    public async Task an_id_token_with_many_audiences_and_the_azp_of_this_client_signs_in()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Open);
        var authorize = await browser.StartWebAsync();
        var login = new StubLogin(NewSubject())
        {
            IdTokenAudiences = [StubOidcProvider.ClientId, "another-client"],
            IdTokenAzp = StubOidcProvider.ClientId,
        };

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, login);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public Task an_expired_id_token_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenLifetime = TimeSpan.FromHours(-1) });

    [Test]
    public Task an_id_token_with_no_expiry_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenHasNoExpiry = true });

    [Test]
    public Task an_id_token_from_another_key_with_the_same_key_id_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenSigning = IdTokenSigning.OtherKeyWithTheSameId });

    [Test]
    public Task an_id_token_with_alg_none_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenSigning = IdTokenSigning.None });

    [Test]
    public Task an_id_token_signed_with_hs256_is_refused()
        => ShouldBeRefusedAsync(new StubLogin(NewSubject()) { IdTokenSigning = IdTokenSigning.Hs256 });
}
