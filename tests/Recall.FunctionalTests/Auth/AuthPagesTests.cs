namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Recall.Server.Auth;
using Shouldly;

public partial class AuthPagesTests
{
    private static AuthBrowser NewBrowser() => new(AuthHosts.Closed);

    [Test]
    public async Task the_signup_closed_page_offers_another_account_and_keeps_the_return_path()
    {
        // Arrange
        using var owner = NewBrowser();
        await owner.LoginWebAsync("owner");
        using var stranger = NewBrowser();
        var authorize = await stranger.StartWebAsync("/entries");

        // Act
        var refused = await stranger.FinishAtProviderAsync(authorize, new StubLogin($"wrong-{Guid.NewGuid():N}"));
        var page = await refused.Content.ReadAsStringAsync();
        var action = WebUtility.HtmlDecode(ActionLink().Match(page).Groups["href"].Value);
        var again = AuthorizeRequest.Of((await stranger.GetAsync(action)).Headers.Location!);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        page.ShouldContain("Use another account");
        page.ShouldContain("sign out at the provider first");
        action.ShouldContain("returnUrl=%2Fentries");
        again.Prompt.ShouldBe("login");
    }

    [Test]
    public async Task a_person_who_uses_the_right_account_after_the_action_gets_in()
    {
        // Arrange
        using var owner = NewBrowser();
        await owner.LoginWebAsync("owner");
        using var person = NewBrowser();
        var refused = await person.FinishAtProviderAsync(await person.StartWebAsync("/entries"), new StubLogin($"wrong-{Guid.NewGuid():N}"));
        var action = WebUtility.HtmlDecode(ActionLink().Match(await refused.Content.ReadAsStringAsync()).Groups["href"].Value);

        // Act
        var authorize = AuthorizeRequest.Of((await person.GetAsync(action)).Headers.Location!);
        var response = await person.FinishAtProviderAsync(authorize, new StubLogin("owner"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/entries");
        person.SessionCookie.ShouldNotBeNull();
    }

    [Test]
    public async Task an_ordinary_start_sends_no_prompt_and_another_prompt_value_is_ignored()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var ordinary = await browser.StartWebAsync();
        var other = AuthorizeRequest.Of((await browser.GetAsync("/auth/oidc/stub/start?client=web&prompt=none")).Headers.Location!);

        // Assert
        ordinary.Prompt.ShouldBeNull();
        other.Prompt.ShouldBeNull();
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("none,login,consent", true)]
    [Arguments("none,consent", false)]
    public async Task prompt_login_goes_out_unless_the_provider_lists_prompts_without_it(string? listed, bool sent)
    {
        // Arrange
        using var provider = new StubOidcProvider { PromptValues = listed?.Split(',') };
        using var mode = new OidcLoginMode(
            "stub",
            new OidcProviderOptions { Issuer = StubOidcProvider.Issuer, ClientId = StubOidcProvider.ClientId, ClientSecret = provider.ClientSecret },
            provider,
            TimeProvider.System);
        var discovery = (await mode.DiscoverAsync(CancellationToken.None)).ShouldNotBeNull();

        // Act
        var url = new Uri(mode.BuildAuthorizeUrl(discovery, "state", "nonce", Secrets.New(), "https://recall.test/auth/oidc/callback", promptLogin: true));

        // Assert
        QueryHelpers.ParseQuery(url.Query).ContainsKey("prompt").ShouldBe(sent);
    }

    [Test]
    public async Task a_start_with_a_bad_value_has_a_way_out()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/oidc/stub/start?client=nope");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var page = await response.Content.ReadAsStringAsync();
        page.ShouldContain("sign-in request");
        page.ShouldContain("href=\"/auth/login\">Try again</a>");
    }

    [Test]
    public async Task each_page_follows_the_theme_at_the_root_and_has_one_width_under_its_own_policy()
    {
        // Arrange
        using var browser = NewBrowser();

        // Act
        var response = await browser.GetAsync("/auth/login?signedOut=1");

        // Assert
        var page = await response.Content.ReadAsStringAsync();
        var style = StyleBlock().Match(page).Groups["style"].Value;
        style.ShouldContain(":root{color-scheme:light dark;");
        style.ShouldContain("@media (prefers-color-scheme:dark)");
        style.ShouldContain("background:var(--background)");
        style.ShouldContain("main{box-sizing:border-box;width:100%;max-width:28rem;");
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain($"style-src 'sha256-{hash}'");
    }

    [GeneratedRegex("<a class=\"button\" href=\"(?<href>[^\"]+)\">Use another account</a>")]
    private static partial Regex ActionLink();

    [GeneratedRegex("<style>(?<style>.*)</style>")]
    private static partial Regex StyleBlock();
}
