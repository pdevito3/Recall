namespace Recall.FunctionalTests.Auth;

using System.Net;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class HostLimitTests
{
    private const int MostStartsInTwoWindows = 40;

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
        codes.Count(code => code == HttpStatusCode.Redirect).ShouldBeLessThanOrEqualTo(MostStartsInTwoWindows);
    }

    [Test]
    public async Task one_flooding_address_leaves_another_address_able_to_start()
    {
        // Arrange
        using var client = AuthHosts.Limited.NewClient();

        // Act
        var flood = await Task.WhenAll(Enumerable.Range(0, 45).Select(_ => client.SendAsync(StartFrom("203.0.113.10"))));
        var other = await client.SendAsync(StartFrom("203.0.113.11"));

        // Assert
        flood.Select(answer => answer.StatusCode).ShouldContain(HttpStatusCode.TooManyRequests);
        other.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task a_new_forwarded_header_on_each_request_gets_no_new_bucket()
    {
        // Arrange
        using var client = AuthHosts.Limited.NewClient();

        // Act
        var answers = await Task.WhenAll(Enumerable.Range(0, 45).Select(index =>
        {
            var request = StartFrom("203.0.113.20");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{index}");
            return client.SendAsync(request);
        }));

        // Assert
        answers.Count(answer => answer.StatusCode == HttpStatusCode.Redirect).ShouldBeLessThanOrEqualTo(MostStartsInTwoWindows);
    }

    [Test]
    [Arguments("192.0.2.1", "192.0.2.1")]
    [Arguments("::ffff:192.0.2.1", "192.0.2.1")]
    [Arguments("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::")]
    [Arguments("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::")]
    public void a_bucket_is_one_ipv4_address_or_one_ipv6_network(string address, string bucket)
        => LoginStartLimit.BucketOf(IPAddress.Parse(address)).ShouldBe(bucket);

    [Test]
    public async Task a_refused_start_is_a_page_that_says_when_to_try_again()
    {
        // Arrange
        using var client = AuthHosts.Limited.NewClient();

        // Act
        var answers = await Task.WhenAll(Enumerable.Range(0, 45).Select(_ => client.SendAsync(StartFrom("203.0.113.30"))));

        // Assert
        var refused = answers.First(answer => answer.StatusCode == HttpStatusCode.TooManyRequests);
        var wait = refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
        wait.ShouldBeInRange(1, 10);
        refused.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var page = await refused.Content.ReadAsStringAsync();
        page.ShouldContain("<h1>Too many sign-in attempts</h1>");
        page.ShouldContain(">Try again</a>");
        page.ShouldContain("/auth/oidc/stub/start?client=web&amp;returnUrl=%2Fentries");
    }

    private static HttpRequestMessage StartFrom(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/oidc/stub/start?client=web&returnUrl=%2Fentries");
        request.Headers.Add(AuthFlowFactory.ConnectionAddressHeader, address);
        return request;
    }
}
