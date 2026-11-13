namespace Recall.FunctionalTests.FunctionalTests.Jobs;

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Recall.FunctionalTests.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Resources.Entrypoints.Auth;
using Serilog.Events;
using TestUtilities;
using Recall.Server.Resources.Entrypoints.Jobs;
using Recall.Server.Resources.Jobs;
using Shouldly;

[NotInParallel(nameof(JobDashboardTests))]
public partial class JobDashboardTests
{
    private const string OwnerSubject = "jobs-owner";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private static async Task<AuthBrowser> SignedInAsync(AuthFlowFactory host, string subject = OwnerSubject)
    {
        var browser = new AuthBrowser(host);
        await browser.LoginWebAsync(subject);
        return browser;
    }

    private static async Task<JobSnapshot> OnlyHealthJobInStateAsync(AuthFlowFactory host, JobState state)
    {
        var monitor = host.Services.GetRequiredService<BackWaveMonitor>();
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            var job = (await monitor.ListJobsAsync(new JobQuery { WireName = "system-health" })).FirstOrDefault(j => j.State == state);
            if (job is not null)
                return job;

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"No health job reached {state} in {WaitLimit}.");

            await Task.Delay(100);
        }
    }

    private static readonly string[] DatabaseFiles = ["test.db", "test.db-wal"];

    private static async Task<HttpResponseMessage> SendRoleAsync(AuthBrowser browser, string userId, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/{ApiRoutes.Users.UpdateRole(userId)}") { Content = JsonContent.Create(new { role }) };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return await browser.SendAsync(request);
    }

    private static void ShouldBeTheRefusalPage(string page)
    {
        page.ShouldContain("Only the Owner can see the jobs");
        page.ShouldContain($"{AuthRoutes.Login}?returnUrl=%2Fbackwave");
        page.ShouldNotContain("system-health");
    }

    private static async Task ExecuteAsync(AuthFlowFactory host, string sql)
    {
        var connectionString = await host.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> TextsAsync(AuthFlowFactory host, string sql, Guid jobId)
    {
        var connectionString = await host.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("$id", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        var texts = new List<string>();
        while (await reader.ReadAsync())
            texts.Add(reader.GetString(0));
        return texts;
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<HttpResponseMessage> HealthThatStoppedAsync(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            var health = await client.GetAsync($"/{ApiRoutes.Health}");
            if (health.StatusCode != HttpStatusCode.OK || DateTimeOffset.UtcNow > deadline)
                return health;

            health.Dispose();
            await Task.Delay(100);
        }
    }

    /// <summary>Code that holds the static Serilog logger writes to the logger of the first host, not to the logger of a later host.</summary>
    private static IEnumerable<LogEvent> AllLogsOf(AuthFlowFactory host) =>
        host.Logs.All.Concat(TestingWebApplicationFactory.Instance.CapturedLogs.All);

    private static async Task<List<string>> FatalLinesAboutStoppedJobsAsync(AuthFlowFactory host)
    {
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            var lines = AllLogsOf(host)
                .Where(entry => entry.Level == LogEventLevel.Fatal)
                .Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture))
                .Where(text => text.Contains("The background jobs stopped"))
                .ToList();
            if (lines.Count > 0 || DateTimeOffset.UtcNow > deadline)
                return lines;

            await Task.Delay(100);
        }
    }

    private static string SpanText(Activity span) => string.Join('\n', new[] { span.StatusDescription ?? "" }
        .Concat(span.TagObjects.Select(tag => $"{tag.Value}"))
        .Concat(span.Events.SelectMany(spanEvent => spanEvent.Tags.Select(tag => $"{tag.Value}"))));

    private static bool IsFailedHealthJobSpan(Activity span) => span.Source.Name == "BackWave"
        && span.Status == ActivityStatusCode.Error
        && span.GetTagItem("messaging.destination.template") is "system-health";

    private static async Task<List<Activity>> FailedHealthJobSpansAsync()
    {
        var factory = TestingWebApplicationFactory.Instance;
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            var spans = factory.ExportedActivities.Snapshot().Where(IsFailedHealthJobSpan).ToList();
            if (spans.Count > 0 || DateTimeOffset.UtcNow > deadline)
                return spans;

            await Task.Delay(100);
        }
    }

    private static async Task<string> BundleWithFailedHealthJobSpanAsync(AuthBrowser owner)
    {
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            using var response = await owner.GetAsync($"/{ApiRoutes.SupportBundle}", csrf: true);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
            var text = new StringBuilder();
            foreach (var entry in zip.Entries)
            {
                await using var content = await entry.OpenAsync();
                using var reader = new StreamReader(content, Encoding.UTF8);
                text.Append(await reader.ReadToEndAsync());
            }

            var bundle = text.ToString();
            if (bundle.Contains("\"status\":\"Error\"") && bundle.Contains("system-health"))
                return bundle;

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("The support bundle has no span of a failed health job.");

            await Task.Delay(500);
        }
    }

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)"|value="([^"]+)"[^>]*name="__RequestVerificationToken" """)]
    private static partial Regex AntiforgeryField();

    private static string AntiforgeryTokenIn(string page)
    {
        var match = AntiforgeryField().Match(page);
        match.Success.ShouldBeTrue("The dashboard page has no antiforgery field.");
        return WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
    }

    [Test]
    public async Task the_owner_opens_the_dashboard_with_only_a_cookie_and_sees_one_finished_health_job()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        await OnlyHealthJobInStateAsync(AuthHosts.Jobs, JobState.Succeeded);

        // Act
        using var page = await owner.GetAsync($"{JobDashboard.Path}/jobs");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("system-health");
        html.ShouldContain("Succeeded");
    }

    [Test]
    public async Task a_member_and_a_second_admin_get_a_403_with_no_job_data()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        using var member = await SignedInAsync(AuthHosts.Jobs, $"jobs-member-{Guid.NewGuid():N}");
        var adminSubject = $"jobs-admin-{Guid.NewGuid():N}";
        using var admin = await SignedInAsync(AuthHosts.Jobs, adminSubject);
        var adminId = await AuthHosts.Jobs.WithDatabaseAsync(database => database.Set<ExternalLogin>()
            .IgnoreQueryFilters()
            .Where(login => login.Subject == adminSubject)
            .Select(login => login.UserId)
            .SingleAsync());
        using var promote = new HttpRequestMessage(HttpMethod.Put, $"/{ApiRoutes.Users.UpdateRole(adminId)}")
        {
            Content = JsonContent.Create(new { role = "Admin" }),
        };
        promote.Headers.Add("X-CSRF", "1");
        promote.Headers.Add(PageUserCheck.Header, await owner.UserOfSessionAsync());
        using var promoted = await owner.SendAsync(promote);
        promoted.IsSuccessStatusCode.ShouldBeTrue();

        // Act
        using var byMember = await member.GetAsync($"{JobDashboard.Path}/jobs");
        using var byAdmin = await admin.GetAsync($"{JobDashboard.Path}/jobs");

        // Assert
        byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ShouldBeTheRefusalPage(await byMember.Content.ReadAsStringAsync());
        byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ShouldBeTheRefusalPage(await byAdmin.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task no_session_gets_a_403_with_no_job_data()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Jobs);

        // Act
        using var overview = await browser.GetAsync(JobDashboard.Path);
        using var jobs = await browser.GetAsync($"{JobDashboard.Path}/jobs");

        // Assert
        overview.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        jobs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ShouldBeTheRefusalPage(await jobs.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task a_refusal_after_a_click_on_another_site_loads_once_more_and_other_refusals_do_not()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Jobs);
        using var fromOtherSite = new HttpRequestMessage(HttpMethod.Get, JobDashboard.Path);
        fromOtherSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var fromThisSite = new HttpRequestMessage(HttpMethod.Get, JobDashboard.Path);
        fromThisSite.Headers.Add("Sec-Fetch-Site", "same-origin");

        // Act
        using var first = await browser.SendAsync(fromOtherSite);
        using var reload = await browser.SendAsync(fromThisSite);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        first.Headers.GetValues("Refresh").ShouldHaveSingleItem().ShouldBe("1");
        ShouldBeTheRefusalPage(await first.Content.ReadAsStringAsync());
        reload.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        reload.Headers.Contains("Refresh").ShouldBeFalse();
        reload.Headers.CacheControl!.NoStore.ShouldBeTrue();
        reload.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        reload.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("default-src 'none'");
    }

    [Test]
    public void a_host_with_no_use_for_a_worker_runs_none()
    {
        // Act
        var withNone = AuthHosts.Open.Services.GetServices<IHostedService>().OfType<JobHostWatch>();
        var withOne = AuthHosts.Jobs.Services.GetServices<IHostedService>().OfType<JobHostWatch>();

        // Assert
        withNone.ShouldBeEmpty();
        withOne.ShouldHaveSingleItem();
    }

    [Test]
    public async Task the_overview_shows_the_throughput_panel()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);

        // Act
        using var overview = await owner.GetAsync(JobDashboard.Path);

        // Assert
        overview.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await overview.Content.ReadAsStringAsync()).ShouldNotContain("AddBackWaveDashboardMetrics");
    }

    [Test]
    public async Task a_live_stream_ends_at_its_limit_and_its_next_request_refuses_a_demoted_owner()
    {
        // Arrange
        var host = AuthHosts.DemotedLiveStream;
        using var owner = await SignedInAsync(host);
        using var next = await SignedInAsync(host, "jobs-next-admin");
        var ids = await host.WithDatabaseAsync(database => database.Set<ExternalLogin>().IgnoreQueryFilters().ToDictionaryAsync(login => login.Subject, login => login.UserId));
        using var promoted = await SendRoleAsync(owner, ids["jobs-next-admin"], "Admin");
        promoted.IsSuccessStatusCode.ShouldBeTrue();
        var clock = Stopwatch.StartNew();

        // Act
        var stream = owner.GetAsync($"{JobDashboard.Path}?live=1");
        await Task.Delay(TimeSpan.FromSeconds(1));
        using var demoted = await SendRoleAsync(next, ids[OwnerSubject], "User");
        using var ended = await stream;
        var streamed = clock.Elapsed;
        using var afterDemotion = await owner.GetAsync($"{JobDashboard.Path}?live=1");

        // Assert
        demoted.IsSuccessStatusCode.ShouldBeTrue();
        ended.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ended.Content.ReadAsStringAsync()).ShouldContain("event:");
        streamed.ShouldBeInRange(JobDashboard.LiveStreamLimit, JobDashboard.LiveStreamLimit * 2);
        afterDemotion.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Logs.All.ShouldNotContain(entry => entry.Level >= LogEventLevel.Error);
    }

    [Test]
    public async Task a_session_cookie_with_no_csrf_header_still_gets_no_api_access()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);

        // Act
        using var withHeader = await owner.GetAsync("/api/v1/users", csrf: true);
        using var withoutHeader = await owner.GetAsync("/api/v1/users");

        // Assert
        withHeader.StatusCode.ShouldBe(HttpStatusCode.OK);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task a_failing_job_retries_to_the_dead_letter_list_and_only_a_valid_owner_post_requeues_it()
    {
        // Arrange
        var host = AuthHosts.FailingJobs;
        using var owner = await SignedInAsync(host);
        using var member = await SignedInAsync(host, $"jobs-member-{Guid.NewGuid():N}");
        var dead = await OnlyHealthJobInStateAsync(host, JobState.DeadLettered);
        dead.Attempt.ShouldBe(3);
        var requeue = $"{JobDashboard.Path}/jobs/{dead.JobId}/requeue";
        using var failures = await owner.GetAsync($"{JobDashboard.Path}/failures");
        var token = AntiforgeryTokenIn(await failures.Content.ReadAsStringAsync());
        var monitor = host.Services.GetRequiredService<BackWaveMonitor>();

        // Act
        using var noToken = await owner.PostFormAsync(requeue, []);
        using var byMember = await member.PostFormAsync(requeue, [("__RequestVerificationToken", token)]);
        var stateBeforeValidPost = (await monitor.GetJobAsync(dead.JobId))!.State;
        using var valid = await owner.PostFormAsync(requeue, [("__RequestVerificationToken", token)]);

        // Assert
        noToken.IsSuccessStatusCode.ShouldBeFalse();
        noToken.StatusCode.ShouldNotBe(HttpStatusCode.SeeOther);
        byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stateBeforeValidPost.ShouldBe(JobState.DeadLettered);
        valid.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        (await monitor.GetJobAsync(dead.JobId))!.State.ShouldNotBe(JobState.DeadLettered);
    }

    [Test]
    public async Task the_health_job_payload_holds_no_text()
    {
        // Arrange
        var job = await OnlyHealthJobInStateAsync(AuthHosts.Jobs, JobState.Succeeded);
        var connectionString = await AuthHosts.Jobs.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT payload FROM backwave_jobs WHERE job_id = $id", connection);
        command.Parameters.AddWithValue("$id", job.JobId.ToString());

        // Act
        var payload = (byte[]?)await command.ExecuteScalarAsync();

        // Assert
        payload.ShouldNotBeNull();
        Encoding.UTF8.GetString(payload).ShouldBe("{}");
    }

    [Test]
    public void the_host_gives_the_workers_their_stop_budget_next_to_the_web_host()
    {
        // Act
        var host = AuthHosts.Jobs.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        // Assert
        host.ServicesStopConcurrently.ShouldBeTrue();
        host.ShutdownTimeout.ShouldBeLessThan(TimeSpan.FromSeconds(10), "The default grace of docker stop is 10 s.");
        JobHosting.JobShutdownBudget.ShouldBeLessThanOrEqualTo(host.ShutdownTimeout * 4 / 5);
    }

    [Test]
    public async Task the_job_span_carries_the_job_name()
    {
        // Arrange
        var factory = TestingWebApplicationFactory.Instance;

        // Act
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (!HasHealthJobSpan(factory) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);

        // Assert
        HasHealthJobSpan(factory).ShouldBeTrue();
    }

    [Test]
    public async Task an_idle_poll_exports_no_span()
    {
        // Arrange
        var factory = TestingWebApplicationFactory.Instance;
        var twoPolls = TimeSpan.FromSeconds(new JobOptions().PollSeconds * 2 + 1);

        // Act
        await Task.Delay(twoPolls);

        // Assert
        factory.ExportedActivities.Snapshot()
            .Where(span => span.Source.Name == "BackWave" && span.GetTagItem("backwave.claimed_count") is 0)
            .ShouldBeEmpty();
    }

    [Test]
    public async Task a_halted_worker_group_answers_503_and_says_to_restart_and_the_journal_still_answers()
    {
        // Arrange
        var host = AuthHosts.HaltedJobs;
        await OnlyHealthJobInStateAsync(host, JobState.Succeeded);
        using var client = host.NewClient();

        // Act
        await ExecuteAsync(host, "DROP TABLE backwave_jobs");
        var health = await HealthThatStoppedAsync(client);

        // Assert
        health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await health.Content.ReadAsStringAsync();
        body.ShouldStartWith("Unhealthy");
        body.ShouldContain("The background jobs stopped (worker group ");
        body.ShouldContain(": Microsoft.Data.Sqlite.SqliteException");
        body.ShouldContain("restart the server");
        body.ShouldNotContain("no such table");
        using var journal = await client.GetAsync(AuthRoutes.Status);
        journal.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fatal = await FatalLinesAboutStoppedJobsAsync(host);
        fatal.ShouldHaveSingleItem().ShouldContain("restart the server");
    }

    [Test]
    public async Task a_thrown_message_reaches_no_store_and_the_type_and_the_frames_do()
    {
        // Arrange
        var host = AuthHosts.FailingJobs;
        using var owner = await SignedInAsync(host);
        var dead = await OnlyHealthJobInStateAsync(host, JobState.DeadLettered);
        var marker = SystemHealthJobs.FailOnPurposeText;
        var thrownType = typeof(InvalidOperationException).FullName!;

        // Act
        var row = await TextsAsync(host, "SELECT terminal_cause FROM backwave_jobs WHERE job_id = $id", dead.JobId);
        var history = await TextsAsync(host, "SELECT failure_detail FROM backwave_job_transitions WHERE job_id = $id AND failure_detail IS NOT NULL", dead.JobId);
        var databaseFolder = await host.WithDatabaseAsync(database => Task.FromResult(Path.GetDirectoryName(new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource)!));
        var databaseBytes = await Task.WhenAll(DatabaseFiles
            .Select(async file => Encoding.Latin1.GetString(await ReadSharedAsync(Path.Combine(databaseFolder, file)))));
        using var jobPage = await owner.GetAsync($"{JobDashboard.Path}/jobs/{dead.JobId}");
        using var failuresPage = await owner.GetAsync($"{JobDashboard.Path}/failures");
        var spans = await FailedHealthJobSpansAsync();
        var bundle = await BundleWithFailedHealthJobSpanAsync(owner);

        // Assert
        row.ShouldHaveSingleItem().ShouldBe(thrownType);
        history.ShouldNotBeEmpty();
        history.ShouldAllBe(detail => detail.Contains(nameof(SystemHealthJobs.SystemHealthAsync)) && detail.Contains(thrownType) && !detail.Contains(marker));
        databaseBytes.ShouldAllBe(bytes => !bytes.Contains(marker));
        (await jobPage.Content.ReadAsStringAsync()).ShouldContain(thrownType);
        (await jobPage.Content.ReadAsStringAsync()).ShouldNotContain(marker);
        (await failuresPage.Content.ReadAsStringAsync()).ShouldNotContain(marker);
        spans.ShouldAllBe(span => !SpanText(span).Contains(marker));
        spans.ShouldContain(span => SpanText(span).Contains(thrownType));
        bundle.ShouldNotContain(marker);
        AllLogsOf(host).ShouldAllBe(entry => !entry.RenderMessage(CultureInfo.InvariantCulture).Contains(marker) && (entry.Exception == null || !entry.Exception.ToString().Contains(marker)));
    }

    [Test]
    public async Task a_write_action_of_the_dashboard_is_only_for_the_owner()
    {
        // Arrange
        using var owner = await SignedInAsync(AuthHosts.Jobs);
        using var member = await SignedInAsync(AuthHosts.Jobs, $"jobs-member-{Guid.NewGuid():N}");
        using var nobody = new AuthBrowser(AuthHosts.Jobs);
        var actions = new[]
        {
            $"{JobDashboard.Path}/jobs/{Guid.NewGuid()}/cancel",
            $"{JobDashboard.Path}/queues/{JobHosting.SystemQueue}/pause",
            $"{JobDashboard.Path}/queues/{JobHosting.SystemQueue}/resume",
            $"{JobDashboard.Path}/schedules/no-such-schedule/trigger",
        };

        foreach (var action in actions)
        {
            // Act
            using var byMember = await member.PostFormAsync(action, []);
            using var byNobody = await nobody.PostFormAsync(action, []);

            // Assert
            byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden, action);
            byNobody.StatusCode.ShouldBe(HttpStatusCode.Forbidden, action);
        }
    }

    private static bool HasHealthJobSpan(TestingWebApplicationFactory factory) => factory.ExportedActivities.Snapshot()
        .Any(span => span.Source.Name == "BackWave"
            && span.DisplayName == $"process {JobHosting.SystemQueue}"
            && span.GetTagItem("messaging.destination.template") is "system-health");
}
