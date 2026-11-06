namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

/// <summary>Each test sets a charset for the whole of one provider, so these tests run one at a time on a host of their own.</summary>
[NotInParallel("two-providers")]
public class ProviderCharsetTests
{
    private const string UnreadableCharset = "utf-7";

    private static AuthFlowFactory Host => AuthHosts.TwoProviders;

    [Test]
    public async Task a_discovery_answer_with_utf_7_shows_that_provider_as_down_and_the_healthy_provider_still_signs_in()
    {
        // Arrange
        Host.Provider.DiscoveryCharset = UnreadableCharset;
        try
        {
            await WaitForCacheOfTheLastProbeAsync();
            using var browser = new AuthBrowser(Host);

            // Act
            var login = await browser.GetAsync("/auth/login");
            var status = await browser.GetAsync("/auth/status");
            var startOfBad = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=web");
            var startOfHealthy = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.OtherModeId}/start?client=web");

            // Assert
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await login.Content.ReadAsStringAsync();
            page.ShouldContain("Stub is not available.");
            page.ShouldContain(">Sign in with Other</a>");
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
            var modes = (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modes");
            modes[0].GetProperty("state").GetString().ShouldBe("unreachable");
            modes[1].GetProperty("state").GetString().ShouldBe("ready");
            startOfBad.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            startOfHealthy.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            startOfHealthy.Headers.Location!.OriginalString.ShouldStartWith(AuthFlowFactory.OtherIssuer);
        }
        finally
        {
            Host.Provider.DiscoveryCharset = null;
            await WaitForCacheOfTheLastProbeAsync();
        }
    }

    [Test]
    public async Task a_discovery_answer_that_cannot_be_decompressed_shows_that_provider_as_down_and_the_healthy_provider_still_signs_in()
    {
        // Arrange
        Host.Provider.DiscoveryBodyIsCorrupt = true;
        try
        {
            await WaitForCacheOfTheLastProbeAsync();
            using var browser = new AuthBrowser(Host);

            // Act
            var login = await browser.GetAsync("/auth/login");
            var status = await browser.GetAsync("/auth/status");
            var startOfBad = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=web");
            var startOfHealthy = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.OtherModeId}/start?client=web");

            // Assert
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await login.Content.ReadAsStringAsync();
            page.ShouldContain("Stub is not available.");
            page.ShouldContain(">Sign in with Other</a>");
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
            var modes = (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modes");
            modes[0].GetProperty("state").GetString().ShouldBe("unreachable");
            modes[1].GetProperty("state").GetString().ShouldBe("ready");
            startOfBad.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            startOfHealthy.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }
        finally
        {
            Host.Provider.DiscoveryBodyIsCorrupt = false;
            await WaitForCacheOfTheLastProbeAsync();
        }
    }

    [Test]
    public async Task a_key_answer_that_cannot_be_decompressed_is_refused()
    {
        // Arrange
        await WaitForCacheOfTheLastProbeAsync();
        using var browser = new AuthBrowser(Host);
        var authorize = await browser.StartWebAsync();
        Host.Provider.JwksBodyIsCorrupt = true;
        try
        {
            // Act
            var response = await browser.FinishAtProviderAsync(authorize, new StubLogin($"subject-{Guid.NewGuid():N}"));

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
            browser.SessionCookie.ShouldBeNull();
        }
        finally
        {
            Host.Provider.JwksBodyIsCorrupt = false;
        }
    }

    [Test]
    public async Task a_key_answer_with_utf_7_is_refused()
    {
        // Arrange
        await WaitForCacheOfTheLastProbeAsync();
        using var browser = new AuthBrowser(Host);
        var authorize = await browser.StartWebAsync();
        Host.Provider.JwksCharset = UnreadableCharset;
        try
        {
            // Act
            var response = await browser.FinishAtProviderAsync(authorize, new StubLogin($"subject-{Guid.NewGuid():N}"));

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
            browser.SessionCookie.ShouldBeNull();
        }
        finally
        {
            Host.Provider.JwksCharset = null;
        }
    }

    // One probe lasts 2 seconds in the Server.
    private static Task WaitForCacheOfTheLastProbeAsync() => Task.Delay(TimeSpan.FromSeconds(2.1));
}
