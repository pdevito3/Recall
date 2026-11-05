namespace Recall.FunctionalTests.Auth;

using System.Net;
using Shouldly;

/// <summary>The bounds of an anonymous caller: the host name and the rate of login starts.</summary>
public class HostLimitTests
{
    [Test]
    public async Task with_allowed_hosts_set_a_forged_host_is_refused_before_a_login_starts()
    {
        // Arrange
        using var client = AuthHosts.Limited.NewClient();
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/auth/status");
        forged.Headers.Host = "evil.test";

        // Act
        var refused = await client.SendAsync(forged);
        var allowed = await client.GetAsync("/auth/status");

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task many_login_starts_at_one_time_meet_the_limit()
    {
        // Arrange
        using var client = AuthHosts.Limited.NewClient();

        // Act
        var answers = await Task.WhenAll(Enumerable.Range(0, 45).Select(_ => client.GetAsync("/auth/oidc/stub/start?client=web")));

        // Assert
        var codes = answers.Select(answer => answer.StatusCode).ToList();
        codes.ShouldContain(HttpStatusCode.TooManyRequests);
        codes.ShouldContain(HttpStatusCode.Redirect);
        // A window boundary can fall in the run, so two windows are the most that can pass
        codes.Count(code => code == HttpStatusCode.Redirect).ShouldBeLessThanOrEqualTo(40);
    }
}
