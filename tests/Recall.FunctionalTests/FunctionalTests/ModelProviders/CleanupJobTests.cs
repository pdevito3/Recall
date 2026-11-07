namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Cleanup;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Domain.Tenants;
using Recall.Server.Resources.Jobs;
using Shouldly;
using TestUtilities;

public class CleanupJobTests : TestBase
{
    private const string Words = "i went to teh market";

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private static async Task<IReadOnlyList<JobSnapshot>> JobsOfAsync(string sessionId)
    {
        var monitor = Services.GetRequiredService<BackWaveMonitor>();
        var jobs = await monitor.ListJobsAsync(new JobQuery { WireName = CleanupJob.WireName, MaxResults = 1000 });
        var mine = new List<JobSnapshot>();
        foreach (var job in jobs)
        {
            var payload = await monitor.GetJobPayloadAsync(job.JobId);
            if (PayloadText(payload).Contains(sessionId, StringComparison.Ordinal))
                mine.Add(job);
        }

        return mine;
    }

    private static string PayloadText(JobPayloadView? view) => view is null ? string.Empty : view.Text;

    private static async Task SetJobStateAsync(string sessionId, JobState state)
        => await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE backwave_jobs SET state = $state WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $session",
            ("$state", (int)state),
            ("$wire", CleanupJob.WireName),
            ("$session", $"%{sessionId}%"));

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_job_stores_the_cleaned_words_as_a_revision_and_raw_stays(ModelProviderKind kind)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, kind);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        started.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        session.CleanedText.ShouldContain("teh market (cleaned)");
        session.RawText.ShouldBe(started.RawText);
        session.RawLatestRevision.ShouldBe(started.RawLatestRevision);
        session.Synopsis.ShouldBe("I wrote some words.");
        session.LastCleanupRawRevision.ShouldBe(1);
        session.Suggestions.Select(s => s.Value).ShouldBe(["work", "Calm"]);
        (await CleanupScenario.CleanedRevisionCountAsync(started.Id)).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_job_payload_holds_no_words_and_no_key()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);

        // Act
        var started = await scenario.SessionWithRunningCleanupAsync($"{Words} {marker}");
        var job = (await JobsOfAsync(started.Id)).ShouldHaveSingleItem();
        var payload = PayloadText(await Services.GetRequiredService<BackWaveMonitor>().GetJobPayloadAsync(job.JobId));

        // Assert
        payload.ShouldContain(started.Id);
        payload.ShouldNotContain(marker);
        payload.ShouldNotContain(scenario.ApiKey);
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_second_run_of_a_finished_job_changes_nothing_and_calls_no_provider()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        await scenario.RunJobAsync(started.Id);
        var before = await scenario.ReadAsync(started.Id);

        // Act
        await scenario.RunJobAsync(started.Id, attempt: 2);
        var after = await scenario.ReadAsync(started.Id);

        // Assert
        scenario.Fake.CompletionCalls.ShouldBe(1);
        after.CleanedLatestRevision.ShouldBe(before.CleanedLatestRevision);
        (await CleanupScenario.CleanedRevisionCountAsync(started.Id)).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_job_of_an_older_run_cannot_end_the_newer_run()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id, runId: "run_older");
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
        scenario.Fake.CompletionCalls.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task two_starts_at_once_make_one_run_and_one_job()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync(Words);

        // Act
        var starts = await Task.WhenAll(
            FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null),
            FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null),
            FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null));
        var jobs = await JobsOfAsync(session.Id);
        await scenario.RunJobAsync(session.Id);

        // Assert
        starts.Select(s => s.StatusCode).ShouldAllBe(status => status == HttpStatusCode.Accepted);
        jobs.Count.ShouldBe(1);
        (await CleanupScenario.CleanedRevisionCountAsync(session.Id)).ShouldBe(1);
    }

    [Test]
    public async Task a_refusal_fails_the_run_at_once_with_a_closed_code()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.EchoError;
        var started = await scenario.SessionWithRunningCleanupAsync(marker);

        // Act
        await scenario.RunJobAsync(started.Id);
        var body = await FactoryClient.GetStringAsync(ApiRoutes.Sessions.GetRecord(started.Id));
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.ProviderRefused));
        body.ShouldNotContain("Cannot read this input");
        scenario.Fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    public async Task an_unavailable_provider_makes_the_job_try_again_with_no_words_of_the_provider()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 503;
        var started = await scenario.SessionWithRunningCleanupAsync(marker);

        // Act
        var run = () => scenario.RunJobAsync(started.Id);
        var thrown = await run.ShouldThrowAsync<JobSafeTextException>();
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        thrown.Message.ShouldNotContain(marker);
        thrown.Message.ShouldNotContain("The fake answers");
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task the_last_call_to_an_unavailable_provider_ends_the_run()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 429;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id, attempt: CleanupAttempt.MaxProviderCalls);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.ProviderUnavailable));
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task a_claim_above_the_limit_ends_the_run_with_no_call()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id, attempt: CleanupAttempt.MaxProviderCalls + 1);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.Interrupted));
        scenario.Fake.CompletionCalls.ShouldBe(0);
    }

    [Test]
    [Arguments(FakeMode.BadJson)]
    [Arguments(FakeMode.Truncate)]
    [Arguments(FakeMode.Refusal)]
    public async Task an_answer_that_the_app_cannot_use_ends_the_run_and_keeps_cleaned(FakeMode mode)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = mode;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.BadModelOutput));
        session.CleanedLatestRevision.ShouldBe(started.CleanedLatestRevision);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.Anthropic, "<html>a page of another service</html>", "text/html")]
    [Arguments(ModelProviderKind.Anthropic, "{}", "application/json; charset=no-such-charset")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "<html>a page of another service</html>", "text/html")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{}", "application/json; charset=no-such-charset")]
    public async Task an_answer_that_will_not_change_on_a_retry_ends_at_the_first_call(ModelProviderKind kind, string body, string contentType)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, kind);
        scenario.Fake.Behavior.Mode = FakeMode.RawBody;
        scenario.Fake.Behavior.Reply = body;
        scenario.Fake.Behavior.ContentType = contentType;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act: a claim that a worker must try again throws, and this one ends with no throw.
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.BadModelOutput));
    }

    [Test]
    public async Task a_stop_ends_a_call_that_hangs_at_once_and_a_person_can_run_it_again()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.Hold;
        scenario.Fake.Behavior.DelayMs = 120_000;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        var call = scenario.RunJobAsync(started.Id);
        while (scenario.Fake.CompletionCalls == 0)
            await Task.Delay(TimeSpan.FromMilliseconds(20));

        // Act
        var stopped = await FactoryClient.PostAsync(ApiRoutes.Sessions.StopCleanup(started.Id), null);
        await call.WaitAsync(TimeSpan.FromSeconds(10));
        var afterStop = await scenario.ReadAsync(started.Id);
        scenario.Fake.Behavior.Mode = FakeMode.Normal;
        var again = await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(started.Id), null);
        await scenario.RunJobAsync(started.Id);
        var afterRun = await scenario.ReadAsync(started.Id);

        // Assert
        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterStop.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        afterStop.CleanupFailure.ShouldBe(nameof(CleanupFailure.Stopped));
        afterStop.RawText.ShouldBe(started.RawText);
        afterStop.CleanedLatestRevision.ShouldBe(started.CleanedLatestRevision);
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        afterRun.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        afterRun.RawText.ShouldBe(started.RawText);
    }

    [Test]
    public async Task an_answer_that_arrives_after_a_stop_changes_nothing()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.Hold;
        scenario.Fake.Behavior.DelayMs = 1_000;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        var runId = await CleanupScenario.RunIdOfAsync(started.Id);

        // Act
        (await FactoryClient.PostAsync(ApiRoutes.Sessions.StopCleanup(started.Id), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await scenario.RunJobAsync(started.Id, runId: runId);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        scenario.Fake.CompletionCalls.ShouldBe(0);
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.Stopped));
        session.CleanedLatestRevision.ShouldBe(started.CleanedLatestRevision);
    }

    [Test]
    public async Task a_stop_with_no_running_cleanup_changes_nothing_and_tells_the_truth()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        await scenario.RunJobAsync(started.Id);
        var clean = await scenario.ReadAsync(started.Id);

        // Act
        var stopped = await FactoryClient.PostAsync(ApiRoutes.Sessions.StopCleanup(started.Id), null);
        var after = await scenario.ReadAsync(started.Id);

        // Assert
        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        after.CleanupFailure.ShouldBe(nameof(CleanupFailure.None));
        after.CleanedLatestRevision.ShouldBe(clean.CleanedLatestRevision);
    }

    [Test]
    public async Task a_key_that_the_ring_cannot_read_ends_the_run_with_no_call()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        await CleanupScenario.SetProtectedKeyAsync(scenario.Provider.Id, CleanupScenario.ProtectedKeyUnderAnotherRing());

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.KeyUnreadable));
        scenario.Fake.CompletionCalls.ShouldBe(0);
    }

    [Test]
    public async Task a_provider_deleted_after_the_start_ends_the_run()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        FactoryClient.SignInAsAppUser(scenario.Admin.Identifier);
        (await FactoryClient.DeleteAsync(ApiRoutes.ModelProviders.GetRecord(scenario.Provider.Id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        FactoryClient.SignInAsAppUser(scenario.Member.Identifier);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.NoModelProvider));
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task a_stop_of_the_host_returns_the_lease_and_the_next_claim_stores_the_result()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.Hold;
        scenario.Fake.Behavior.DelayMs = 60_000;
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act
        var stopped = () => scenario.RunJobAsync(started.Id, cancellationToken: stop.Token);
        await stopped.ShouldThrowAsync<OperationCanceledException>();
        var during = await scenario.ReadAsync(started.Id);
        scenario.Fake.Behavior.Mode = FakeMode.Normal;
        await scenario.RunJobAsync(started.Id, attempt: 2);
        var after = await scenario.ReadAsync(started.Id);

        // Assert
        during.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
    }

    [Test]
    [Property("Rule", "SES-37")]
    [Arguments(JobState.DeadLettered)]
    [Arguments(JobState.Cancelled)]
    [Arguments(JobState.Quarantined)]
    public async Task the_watch_ends_the_run_of_a_job_that_ended_with_no_result(JobState state)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        await SetJobStateAsync(started.Id, state);
        var watch = ActivatorUtilities.CreateInstance<CleanupJobWatch>(Services);

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.Interrupted));
    }

    [Test]
    public async Task the_watch_reads_past_a_full_page_of_ended_jobs()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        SessionDto? last = null;
        for (var i = 0; i <= 200; i++)
        {
            last = await scenario.SessionWithRunningCleanupAsync($"{Words} {i}");
            await SetJobStateAsync(last.Id, JobState.Cancelled);
        }

        var watch = ActivatorUtilities.CreateInstance<CleanupJobWatch>(Services);

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var session = await scenario.ReadAsync(last!.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
    }

    [Test]
    [Property("Rule", "SES-37")]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("""{"sessionId":"ses_x","userId":"usr_x","tenantId":"not-a-tenant","runId":"run_x"}""")]
    public async Task a_payload_that_does_not_read_stops_only_itself(string badPayload)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var broken = await scenario.SessionWithRunningCleanupAsync(Words);
        var good = await scenario.SessionWithRunningCleanupAsync(Words);
        await SetJobStateAsync(broken.Id, JobState.Quarantined);
        await SetJobStateAsync(good.Id, JobState.Quarantined);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE backwave_jobs SET payload = $bad WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $session",
            ("$bad", System.Text.Encoding.UTF8.GetBytes(badPayload)),
            ("$wire", CleanupJob.WireName),
            ("$session", $"%{broken.Id}%"));
        var watch = ActivatorUtilities.CreateInstance<CleanupJobWatch>(Services);

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var session = await scenario.ReadAsync(good.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task the_watch_leaves_a_run_that_got_a_result_before_the_job_ended()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);
        await scenario.RunJobAsync(started.Id);
        await SetJobStateAsync(started.Id, JobState.DeadLettered);
        var watch = ActivatorUtilities.CreateInstance<CleanupJobWatch>(Services);

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        session.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
    }

    [Test]
    public async Task a_journal_scope_sees_the_rows_of_its_owner_and_no_others()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var mine = await scenario.SessionWithWordsAsync(Words);
        FactoryClient.SignInAsAppUser(scenario.Admin.Identifier);
        var other = await FactoryClient.AddSessionAsync();
        var tenantId = await CleanupScenario.TenantIdOfAsync(mine.Id);

        // Act
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateJournalScope(new Recall.Server.Databases.JournalOwner(scenario.Member.Id, tenantId));
        var db = scope.ServiceProvider.GetRequiredService<Recall.Server.Databases.AppDbContext>();
        var seen = await db.Sessions.AsNoTracking().Select(s => s.Id).ToListAsync();

        // Assert
        seen.ShouldContain(mine.Id);
        seen.ShouldNotContain(other.Id);
    }

    [Test]
    public async Task the_stream_shows_the_end_of_the_run_and_a_closed_stream_leaves_the_job()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync(Words);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Sessions.CleanupStream(session.Id));
        using var opened = await FactoryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await opened.Content.ReadAsStreamAsync());
        var first = await ReadEventAsync(reader);
        await scenario.RunJobAsync(session.Id);
        var last = await ReadEventAsync(reader);

        // Assert
        opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        opened.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        opened.Headers.CacheControl!.NoStore.ShouldBeTrue();
        first.ShouldContain("\"type\":\"run.started\"");
        last.ShouldContain("\"type\":\"completed\"");
        last.ShouldNotContain("(cleaned)");
    }

    [Test]
    public async Task a_stream_shows_a_failure_as_a_closed_code()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Mode = FakeMode.EchoError;
        var session = await scenario.SessionWithWordsAsync(marker);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Sessions.CleanupStream(session.Id));
        using var opened = await FactoryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await opened.Content.ReadAsStreamAsync());
        await ReadEventAsync(reader);
        await scenario.RunJobAsync(session.Id);
        var last = await ReadEventAsync(reader);

        // Assert
        last.ShouldContain("\"type\":\"failed\"");
        last.ShouldContain(nameof(CleanupFailure.ProviderRefused));
        last.ShouldNotContain(marker);
    }

    private static async Task<string> ReadEventAsync(StreamReader reader)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await reader.ReadLineAsync(limit.Token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
                return line;
        }

        throw new InvalidOperationException("The stream ended with no event.");
    }
}
