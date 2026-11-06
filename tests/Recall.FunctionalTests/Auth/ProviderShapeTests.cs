namespace Recall.FunctionalTests.Auth;

using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Recall.Server.Auth;
using Shouldly;

public class ProviderShapeTests
{
    private static string NewSubject() => $"provider-shape-{Guid.NewGuid():N}";

    private static OidcLoginMode ModeOf(StubOidcProvider provider) => new(
        "stub",
        new OidcProviderOptions { Issuer = StubOidcProvider.Issuer, ClientId = StubOidcProvider.ClientId, ClientSecret = provider.ClientSecret },
        provider,
        TimeProvider.System);

    [Test]
    [Arguments("https://other.test", LoginModeState.Unreachable)]
    [Arguments("https://IDP.test", LoginModeState.Unreachable)]
    [Arguments("http://idp.test", LoginModeState.Unreachable)]
    [Arguments("https://idp.test/", LoginModeState.Ready)]
    public async Task discovery_must_name_the_issuer_of_the_setting(string issuer, LoginModeState expected)
    {
        // Arrange
        using var provider = new StubOidcProvider { DiscoveryIssuer = issuer };
        using var mode = ModeOf(provider);

        // Act
        var state = await mode.ProbeAsync(CancellationToken.None);

        // Assert
        state.ShouldBe(expected);
    }

    [Test]
    public async Task a_provider_that_does_not_list_iss_signs_in_with_no_iss()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.SecretInFormProvider);
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()), iss: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public async Task a_provider_that_does_not_list_iss_still_has_a_wrong_iss_refused()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.SecretInFormProvider);
        var authorize = await browser.StartWebAsync();

        // Act
        var response = await browser.FinishAtProviderAsync(authorize, new StubLogin(NewSubject()), iss: "https://other.test");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_callback_with_two_iss_values_is_refused()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Open);
        var authorize = await browser.StartWebAsync();
        var code = browser.Provider.Approve(authorize, new StubLogin(NewSubject()));
        var url = QueryHelpers.AddQueryString("/auth/oidc/callback", new Dictionary<string, string?> { ["state"] = authorize.State, ["code"] = code, ["iss"] = StubOidcProvider.Issuer });

        // Act
        var response = await browser.GetAsync($"{url}&iss={Uri.EscapeDataString(StubOidcProvider.Issuer)}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    public async Task a_provider_that_lists_only_the_post_method_gets_the_secret_in_the_form()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.SecretInFormProvider);

        // Act
        await browser.LoginWebAsync(NewSubject());

        // Assert
        browser.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public async Task the_first_login_after_the_provider_rolls_its_key_signs_in()
    {
        // Arrange
        using var before = new AuthBrowser(AuthHosts.RollingKeyProvider);
        await before.LoginWebAsync(NewSubject());
        using var after = new AuthBrowser(AuthHosts.RollingKeyProvider);
        after.Provider.RollSigningKey();

        // Act
        await after.LoginWebAsync(NewSubject());

        // Assert
        after.SessionCookie.ShouldNotBeNull();
    }
}
