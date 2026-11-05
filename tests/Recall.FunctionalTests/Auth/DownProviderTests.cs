namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

public class DownProviderTests
{
    [Test]
    public async Task with_the_provider_down_a_start_gives_503_and_each_session_keeps_working()
    {
        // Arrange
        var host = AuthHosts.Failing;
        using var browser = new AuthBrowser(host);
        await browser.LoginWebAsync("web-person");
        using var phone = new AuthBrowser(host);
        var tokens = await phone.LoginNativeAsync("phone-person");

        // Act
        host.Provider.Down = true;
        await WaitForDegradedAsync(browser);
        var startJson = await browser.GetAsync("/auth/oidc/stub/start?client=web");
        using var htmlRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/oidc/stub/start?client=web");
        htmlRequest.Headers.Accept.ParseAdd("text/html");
        var startHtml = await browser.SendAsync(htmlRequest);
        var login = await new AuthBrowser(host).GetAsync("/auth/login?returnUrl=%2Fentries");
        var webSession = await browser.GetAsync("/auth/me", csrf: true);
        var nativeSession = await phone.GetAsync("/auth/me", authorization: $"Bearer {tokens.AccessToken}");
        var refresh = await phone.RefreshAsync(tokens.RefreshToken);

        // Assert
        startJson.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await startJson.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("idp_unreachable");
        startHtml.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        startHtml.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        login.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var loginPage = await login.Content.ReadAsStringAsync();
        loginPage.ShouldContain("<h1>Sign-in is not available</h1>");
        loginPage.ShouldContain("Stub is not available.");
        loginPage.ShouldNotContain("Existing sessions");
        loginPage.ShouldContain("href=\"/auth/login?returnUrl=%2Fentries\">Try again</a>");
        var startPage = await startHtml.Content.ReadAsStringAsync();
        startPage.ShouldNotContain("Existing sessions");
        startPage.ShouldContain(">Try again</a>");
        webSession.StatusCode.ShouldBe(HttpStatusCode.OK);
        nativeSession.StatusCode.ShouldBe(HttpStatusCode.OK);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task WaitForDegradedAsync(AuthBrowser browser)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await browser.GetAsync("/auth/status");
            var body = await status.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("state").GetString() == "degraded")
            {
                body.GetProperty("modes")[0].GetProperty("state").GetString().ShouldBe("unreachable");
                body.GetProperty("message").GetString().ShouldBe("Stub is not available. Existing sessions keep working.");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new InvalidOperationException("The status did not change to degraded.");
    }
}
