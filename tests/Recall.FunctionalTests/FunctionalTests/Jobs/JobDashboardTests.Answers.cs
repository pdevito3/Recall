namespace Recall.FunctionalTests.FunctionalTests.Jobs;

using System.Net;
using Microsoft.Data.Sqlite;
using Recall.FunctionalTests.Auth;
using Recall.Server.Resources.Entrypoints.Jobs;
using Recall.Server.Resources.Jobs;
using Serilog.Events;
using Shouldly;

public partial class JobDashboardTests
{
    private const string AntiforgeryCookiePrefix = ".AspNetCore.Antiforgery";

    private static readonly string[] DashboardGets =
    [
        JobDashboard.Path,
        $"{JobDashboard.Path}/",
        "/BACKWAVE/jobs",
        $"{JobDashboard.Path}/jobs",
        $"{JobDashboard.Path}/failures",
        $"{JobDashboard.Path}/queues",
        $"{JobDashboard.Path}/executing",
        $"{JobDashboard.Path}/observers",
        $"{JobDashboard.Path}/schedules",
        $"{JobDashboard.Path}/tags/suggest?q=a",
        $"{JobDashboard.Path}/no-such-page",
    ];

    private static void ShouldRefuseEachFrameAndStoreNothing(HttpResponseMessage answer, string what)
    {
        answer.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY", what);
        answer.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("frame-ancestors 'none'", Case.Sensitive, what);
        answer.Headers.CacheControl!.NoStore.ShouldBeTrue(what);
    }

    private static void DropAntiforgeryCookies(AuthBrowser browser)
    {
        foreach (var name in browser.Cookies.Keys.Where(name => name.StartsWith(AntiforgeryCookiePrefix, StringComparison.Ordinal)).ToList())
            browser.DropCookie(name);
    }

    private static async Task ShouldRefuseFramesOnEachAnswerAsync(AuthBrowser browser, string who)
    {
        foreach (var path in DashboardGets)
        {
            DropAntiforgeryCookies(browser);
            using var noCookie = await browser.GetAsync(path);
            ShouldRefuseEachFrameAndStoreNothing(noCookie, $"{who}, no antiforgery cookie, {path}, {noCookie.StatusCode}");

            using var withCookie = await browser.GetAsync(path);
            ShouldRefuseEachFrameAndStoreNothing(withCookie, $"{who}, {path}, {withCookie.StatusCode}");
        }

        var actions = new[] { $"{JobDashboard.Path}/jobs/{Guid.NewGuid()}/requeue", $"{JobDashboard.Path}/queues/{JobHosting.SystemQueue}/pause", $"{JobDashboard.Path}/no-such-action" };
        foreach (var action in actions)
        {
            using var post = await browser.PostFormAsync(action, []);
            ShouldRefuseEachFrameAndStoreNothing(post, $"{who}, POST {action}, {post.StatusCode}");
        }

        using var head = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Head, JobDashboard.Path));
        ShouldRefuseEachFrameAndStoreNothing(head, $"{who}, HEAD, {head.StatusCode}");
        using var put = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Put, JobDashboard.Path));
        ShouldRefuseEachFrameAndStoreNothing(put, $"{who}, PUT, {put.StatusCode}");
    }

    /// <summary>The client of the browser reads an answer to its end, and a live stream holds its answer open for 30 s.</summary>
    private static async Task<HttpResponseMessage> OpenLiveStreamAsync(AuthFlowFactory host, AuthBrowser browser, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{JobDashboard.Path}/executing?live=1");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", browser.Cookies.Select(cookie => $"{cookie.Key}={cookie.Value.Value}")));
        return await host.NewClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    [Test]
    public async Task each_answer_of_the_dashboard_refuses_each_frame_for_each_role_and_cookie_state()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        using var member = await SignedInAsync(AuthHosts.Jobs, $"jobs-member-{Guid.NewGuid():N}");
        using var nobody = new AuthBrowser(AuthHosts.Jobs);

        // Act and assert
        await ShouldRefuseFramesOnEachAnswerAsync(owner, "owner");
        await ShouldRefuseFramesOnEachAnswerAsync(member, "member");
        await ShouldRefuseFramesOnEachAnswerAsync(nobody, "nobody");
    }

    [Test]
    public async Task a_refused_live_stream_also_refuses_each_frame()
    {
        // Arrange
        using var nobody = new AuthBrowser(AuthHosts.Jobs);

        // Act
        using var live = await nobody.GetAsync($"{JobDashboard.Path}?live=1");

        // Assert
        live.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await live.Content.ReadAsStringAsync()).ShouldBeEmpty();
        ShouldRefuseEachFrameAndStoreNothing(live, "refused live stream");
    }

    [Test]
    public async Task a_live_stream_stores_nothing_and_refuses_each_frame_and_still_sends_updates()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        using var limit = new CancellationTokenSource(WaitLimit);

        // Act
        using var live = await OpenLiveStreamAsync(AuthHosts.Jobs, owner, limit.Token);
        await using var body = await live.Content.ReadAsStreamAsync(limit.Token);
        using var reader = new StreamReader(body);
        var firstLine = await reader.ReadLineAsync(limit.Token);

        // Assert
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        live.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        ShouldRefuseEachFrameAndStoreNothing(live, "live stream");
        firstLine.ShouldBe("event: update");
    }

    [Test]
    public async Task the_dashboard_policy_keeps_the_frame_refusal_and_the_policy_of_the_refusal_page_stays_strict()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        using var nobody = new AuthBrowser(AuthHosts.Jobs);

        // Act
        using var page = await owner.GetAsync(JobDashboard.Path);
        using var refusal = await nobody.GetAsync(JobDashboard.Path);

        // Assert
        page.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe(JobDashboard.ContentSecurityPolicy);
        var refusalPolicy = refusal.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        refusalPolicy.ShouldNotContain("unsafe-inline");
        refusalPolicy.ShouldContain("frame-ancestors 'none'");
    }

    [Test]
    public async Task a_refused_write_action_shows_the_refusal_page_with_a_way_back_and_does_not_load_again()
    {
        // Arrange
        using var member = await SignedInAsync(AuthHosts.Jobs, $"jobs-member-{Guid.NewGuid():N}");
        using var nobody = new AuthBrowser(AuthHosts.Jobs);
        var action = $"{JobDashboard.Path}/jobs/{Guid.NewGuid()}/requeue";
        using var fromOtherSite = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent([]) };
        fromOtherSite.Headers.Add("Sec-Fetch-Site", "cross-site");

        // Act
        using var byMember = await member.PostFormAsync(action, []);
        using var byNobody = await nobody.SendAsync(fromOtherSite);

        // Assert
        foreach (var refused in new[] { byMember, byNobody })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            refused.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
            refused.Headers.Contains("Refresh").ShouldBeFalse();
            ShouldBeTheRefusalPage(await refused.Content.ReadAsStringAsync());
        }
    }

    [Test]
    public async Task a_lost_job_table_shows_a_page_that_says_what_to_do_and_the_log_keeps_the_cause()
    {
        // Arrange
        var host = AuthHosts.LostJobTable;
        using var owner = await SignedInAsync(host);
        using var before = await owner.GetAsync($"{JobDashboard.Path}/jobs");
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ExecuteAsync(host, "DROP TABLE backwave_jobs");

        // Act
        using var page = await owner.GetAsync($"{JobDashboard.Path}/jobs");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("The job pages cannot open");
        html.ShouldContain("docs/troubleshooting.md");
        html.ShouldContain("The background jobs stopped");
        html.ShouldNotContain("no such table");
        ShouldRefuseEachFrameAndStoreNothing(page, "fault page");
        AllLogsOf(host).ShouldContain(entry => entry.Level == LogEventLevel.Error && entry.Exception is SqliteException);
    }
}
