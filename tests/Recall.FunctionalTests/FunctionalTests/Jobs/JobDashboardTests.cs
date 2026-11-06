namespace Recall.FunctionalTests.FunctionalTests.Jobs;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Recall.FunctionalTests.Auth;
using Recall.Server.Auth.Entities;
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
        using var promoted = await owner.SendAsync(promote);
        promoted.IsSuccessStatusCode.ShouldBeTrue();

        // Act
        using var byMember = await member.GetAsync($"{JobDashboard.Path}/jobs");
        using var byAdmin = await admin.GetAsync($"{JobDashboard.Path}/jobs");

        // Assert
        byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await byMember.Content.ReadAsStringAsync()).ShouldBeEmpty();
        byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await byAdmin.Content.ReadAsStringAsync()).ShouldBeEmpty();
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
        (await jobs.Content.ReadAsStringAsync()).ShouldBeEmpty();
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
    public void the_host_stops_inside_the_docker_stop_grace()
    {
        // Act
        var host = AuthHosts.Jobs.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        // Assert
        host.ShutdownTimeout.ShouldBe(JobHosting.HostShutdownTimeout);
        host.ShutdownTimeout.ShouldBeLessThan(JobHosting.DockerStopGrace);
        host.ServicesStopConcurrently.ShouldBeTrue();
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
    public void the_health_endpoint_reports_a_halted_worker_group()
    {
        // Act
        var checks = AuthHosts.Jobs.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        // Assert
        checks.ShouldContain(check => check.Name == JobHosting.HealthCheckName);
    }

    private static bool HasHealthJobSpan(TestingWebApplicationFactory factory) => factory.ExportedActivities.Snapshot()
        .Any(span => span.Source.Name == "BackWave"
            && span.DisplayName == $"process {JobHosting.SystemQueue}"
            && span.GetTagItem("messaging.destination.template") is "system-health");
}
