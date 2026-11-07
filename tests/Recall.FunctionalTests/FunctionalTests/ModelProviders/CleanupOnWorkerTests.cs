namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BackWave;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.Auth;
using Recall.FunctionalTests.FunctionalTests.Recordings;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Dtos;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Resources.Entrypoints.Jobs;
using Recall.Server.Resources.Jobs;
using Shouldly;
using TestUtilities;

/// <summary>
/// The whole path on a host with a real worker. Each test connects its own fake as the newest provider, so they run one at a time.
/// A job that an earlier test left to try again calls the newest fake too, so a test counts only the calls that hold its own words.
/// </summary>
[NotInParallel(nameof(CleanupOnWorkerTests))]
public class CleanupOnWorkerTests
{
    private const string OwnerSubject = "cleanup-owner";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private static AuthFlowFactory Host => AuthHosts.Cleanups;

    private static async Task<AuthBrowser> OwnerAsync()
    {
        var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync(OwnerSubject);
        return browser;
    }

    private static async Task<HttpResponseMessage> SendAsync(AuthBrowser browser, HttpMethod method, string route, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"/{route}");
        request.Headers.Add("X-CSRF", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await browser.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage answer, HttpStatusCode expected)
        where T : class
    {
        answer.StatusCode.ShouldBe(expected, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<T>()).ShouldNotBeNull();
    }

    private static async Task ConnectAsync(AuthBrowser owner, FakeModelProvider fake, string key)
    {
        var provider = await ReadAsync<ModelProviderDto>(
            await SendAsync(owner, HttpMethod.Post, ApiRoutes.ModelProviders.Root, new { kind = nameof(ModelProviderKind.Anthropic), name = "Mine", baseUrl = fake.AnthropicBaseUrl, apiKey = key }),
            HttpStatusCode.OK);
        await ReadAsync<ModelProviderDto>(
            await SendAsync(owner, HttpMethod.Put, ApiRoutes.ModelProviders.GetRecord(provider.Id), new { name = provider.Name, baseUrl = provider.BaseUrl, model = FakeModelProvider.ModelId, makeActive = true, version = provider.Version }),
            HttpStatusCode.OK);
    }

    private static async Task<SessionDto> SessionWithWordsAsync(AuthBrowser owner, string words)
    {
        var session = await ReadAsync<SessionDto>(
            await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = DateTimeOffset.UtcNow }),
            HttpStatusCode.Created);
        (await SendAsync(owner, HttpMethod.Put, ApiRoutes.Sessions.Draft(session.Id), new { text = words, baseRevision = 0, savedAt = DateTimeOffset.UtcNow })).StatusCode.ShouldBe(HttpStatusCode.OK);
        return session;
    }

    private static async Task<SessionDto> ReadSessionAsync(AuthBrowser owner, string id)
        => await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Get, ApiRoutes.Sessions.GetRecord(id)), HttpStatusCode.OK);

    private static async Task<SessionDto> SessionAfterRunAsync(AuthBrowser owner, string id)
    {
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            var session = await ReadSessionAsync(owner, id);
            if (session.CleanupStatus != nameof(CleanupStatus.Running))
                return session;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Session {id} stayed Running for {WaitLimit}.");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static async Task<string> TextOfJobTablesAsync()
    {
        var connectionString = await Host.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var list = new SqliteCommand("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'backwave%'", connection))
        await using (var names = await list.ExecuteReaderAsync())
        {
            while (await names.ReadAsync())
                tables.Add(names.GetString(0));
        }

        var dump = new StringBuilder();
        foreach (var table in tables)
        {
            await using var read = new SqliteCommand($"SELECT * FROM \"{table}\"", connection);
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync())
            {
                for (var column = 0; column < rows.FieldCount; column++)
                {
                    dump.Append(rows.GetValue(column) switch
                    {
                        byte[] bytes => Encoding.UTF8.GetString(bytes),
                        var value => value?.ToString(),
                    }).Append('|');
                }
            }
        }

        return dump.ToString();
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_person_leaves_during_the_run_and_later_finds_the_cleaned_words_with_raw_unchanged()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.DelayMs = 1_500;
        string sessionId;
        string raw;

        // Act
        using (var owner = await OwnerAsync())
        {
            await ConnectAsync(owner, fake, key);
            var session = await SessionWithWordsAsync(owner, "i went to teh market");
            sessionId = session.Id;
            var started = await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id)), HttpStatusCode.Accepted);
            started.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
            raw = started.RawText;
        }

        using var later = await OwnerAsync();
        var finished = await SessionAfterRunAsync(later, sessionId);

        // Assert
        finished.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        finished.CleanedText.ShouldContain("teh market (cleaned)");
        finished.RawText.ShouldBe(raw);
        fake.RequestBodies.Count(body => body.Contains("teh market", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-42")]
    public async Task a_person_stops_a_hung_call_on_the_page_and_runs_it_again_on_a_worker()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.Mode = FakeMode.Hold;
        fake.Behavior.DelayMs = 300_000;
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var marker = $"stopped{Guid.NewGuid():N}";
        var session = await SessionWithWordsAsync(owner, marker);
        var raw = (await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id)), HttpStatusCode.Accepted)).RawText;
        await WaitUntilAsync(() => fake.RequestBodies.Count(body => body.Contains(marker, StringComparison.Ordinal)) == 1);

        // Act
        var stopped = await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.StopCleanup(session.Id)), HttpStatusCode.OK);
        fake.Behavior.Mode = FakeMode.Normal;
        await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id)), HttpStatusCode.Accepted);
        var finished = await SessionAfterRunAsync(owner, session.Id);

        // Assert
        stopped.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        stopped.CleanupFailure.ShouldBe(nameof(CleanupFailure.Stopped));
        stopped.RawText.ShouldBe(raw);
        finished.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        finished.RawText.ShouldBe(raw);
        fake.RequestBodies.Count(body => body.Contains(marker, StringComparison.Ordinal)).ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-43")]
    public async Task hung_calls_of_one_queue_hold_two_workers_and_leave_the_others_to_other_queues()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        var hung = $"hung{Guid.NewGuid():N}";
        fake.Behavior.Mode = FakeMode.Hold;
        fake.Behavior.HoldOnly = hung;
        fake.Behavior.DelayMs = 300_000;
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var sessions = new List<SessionDto>();
        for (var i = 0; i < CleanupQueues.JobsAtOnceInAQueue + 2; i++)
        {
            var session = await SessionWithWordsAsync(owner, $"{hung} {i}");
            await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id)), HttpStatusCode.Accepted);
            sessions.Add(session);
        }

        await WaitUntilAsync(() => fake.RequestBodies.Count(body => body.Contains(hung, StringComparison.Ordinal)) >= CleanupQueues.JobsAtOnceInAQueue);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var callsWhileFull = fake.RequestBodies.Count(body => body.Contains(hung, StringComparison.Ordinal));

        // Act
        var free = await SessionWithWordsAsync(owner, $"free{Guid.NewGuid():N}");
        await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(free.Id)), HttpStatusCode.Accepted);
        var work = await Host.WithDatabaseAsync(database => database.Sessions.IgnoreQueryFilters().Where(s => s.Id == free.Id).Select(s => new { s.UserId, s.TenantId, s.CleanupRunId }).FirstAsync());
        var otherQueue = CleanupQueues.All.First(queue => queue != CleanupQueues.Of(work.TenantId));
        await Host.Services.GetRequiredService<BackWaveClient>().EnqueueAsync(new CleanupSession(free.Id, work.UserId, work.TenantId, work.CleanupRunId!), DateTimeOffset.UtcNow, otherQueue);
        var finished = await SessionAfterRunAsync(owner, free.Id);
        var stillHung = await ReadSessionAsync(owner, sessions[0].Id);
        foreach (var session in sessions)
            await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.StopCleanup(session.Id)), HttpStatusCode.OK);

        // Assert
        callsWhileFull.ShouldBe(CleanupQueues.JobsAtOnceInAQueue);
        finished.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        stillHung.CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"The condition did not hold in {WaitLimit}.");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    [Test]
    [Property("Rule", "REC-13")]
    public async Task a_long_transcription_does_not_delay_a_cleanup_and_its_later_chunk_makes_the_result_stale()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        var engine = AuthHosts.HeldTranscription;
        engine.Hold();
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var session = await SessionWithWordsAsync(owner, "words beside a long transcription");
        var recording = await ReadAsync<RecordingDto>(
            await SendAsync(owner, HttpMethod.Post, ApiRoutes.Recordings.OfSession(session.Id), new { clientRecordingId = Guid.NewGuid().ToString(), format = "Webm" }),
            HttpStatusCode.Created);
        using var slice = new HttpRequestMessage(HttpMethod.Put, $"/{ApiRoutes.Recordings.Slice(recording.Id, 0)}") { Content = new ByteArrayContent(RecordingScenario.WebmStart) };
        slice.Headers.Add("X-CSRF", "1");
        slice.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        (await owner.SendAsync(slice)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(owner, HttpMethod.Post, ApiRoutes.Recordings.Finish(recording.Id), new { sliceCount = 1, byteLength = RecordingScenario.WebmStart.Length })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await engine.Entered.WaitAsync(WaitLimit);

        // Act
        await ReadAsync<SessionDto>(await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id)), HttpStatusCode.Accepted);
        var cleaned = await SessionAfterRunAsync(owner, session.Id);
        var transcriptionWasHeld = !engine.IsReleased;
        engine.Release();
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        var afterChunk = await ReadSessionAsync(owner, session.Id);
        while (!afterChunk.RawText.Contains(HeldTranscriptionEngine.Words, StringComparison.Ordinal) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            afterChunk = await ReadSessionAsync(owner, session.Id);
        }

        // Assert
        transcriptionWasHeld.ShouldBeTrue();
        cleaned.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        afterChunk.RawText.ShouldContain(HeldTranscriptionEngine.Words);
        afterChunk.CleanedText.ShouldBe(cleaned.CleanedText);
        afterChunk.CleanupStatus.ShouldBe(nameof(CleanupStatus.Stale));
    }

    [Test]
    public async Task a_stream_that_closes_leaves_the_job_and_the_result_still_arrives()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.DelayMs = 1_500;
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var session = await SessionWithWordsAsync(owner, "words of a stream");

        // Act
        using (var closing = new CancellationTokenSource())
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/{ApiRoutes.Sessions.CleanupStream(session.Id)}");
            request.Headers.Add("X-CSRF", "1");
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", owner.Cookies.Select(cookie => $"{cookie.Key}={cookie.Value.Value}")));
            using var opened = await Host.NewClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, closing.Token);
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
            await closing.CancelAsync();
        }

        var finished = await SessionAfterRunAsync(owner, session.Id);

        // Assert
        finished.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
    }

    [Test]
    [Property("Rule", "SES-38")]
    [Property("Rule", "SES-19")]
    public async Task a_failed_claim_leaves_no_words_and_no_key_in_the_job_tables_or_on_the_dashboard()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.Mode = FakeMode.Status;
        fake.Behavior.Status = 503;
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var session = await SessionWithWordsAsync(owner, marker);

        // Act
        (await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var monitor = Host.Services.GetRequiredService<BackWaveMonitor>();
        JobSnapshot? job = null;
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (job is null && DateTimeOffset.UtcNow < deadline)
        {
            foreach (var candidate in await monitor.ListJobsAsync(new JobQuery { WireName = CleanupJob.WireName, MaxResults = 1000 }))
            {
                var isMine = (await monitor.GetJobPayloadAsync(candidate.JobId))?.Text.Contains(session.Id, StringComparison.Ordinal) == true;
                if (isMine && candidate.State == JobState.Scheduled && candidate.Attempt >= 1)
                    job = candidate;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        job.ShouldNotBeNull("The job did not fail its first claim in time.");
        var tables = await TextOfJobTablesAsync();
        var pages = new List<string>();
        foreach (var page in new[] { JobDashboard.Path, $"{JobDashboard.Path}/jobs", $"{JobDashboard.Path}/failures", $"{JobDashboard.Path}/jobs/{job.JobId}" })
        {
            using var answer = await owner.GetAsync(page);
            answer.StatusCode.ShouldBe(HttpStatusCode.OK, page);
            pages.Add(await answer.Content.ReadAsStringAsync());
        }

        // Assert
        tables.ShouldContain("The model provider did not answer.");
        tables.ShouldNotContain(marker);
        tables.ShouldNotContain(key);
        tables.ShouldNotContain("The fake answers");
        pages[3].ShouldContain(CleanupJob.WireName);
        foreach (var page in pages)
        {
            page.ShouldNotContain(marker);
            page.ShouldNotContain(key);
        }

        (await ReadSessionAsync(owner, session.Id)).CleanupStatus.ShouldBe(nameof(CleanupStatus.Running));
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task a_job_on_the_dead_letter_list_ends_its_run_through_the_watch()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.Mode = FakeMode.Hold;
        fake.Behavior.DelayMs = 5_000;
        using var owner = await OwnerAsync();
        await ConnectAsync(owner, fake, key);
        var session = await SessionWithWordsAsync(owner, "words of a dead job");
        (await SendAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var connectionString = await Host.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var update = new SqliteCommand("UPDATE backwave_jobs SET state = $state WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $session", connection);
        update.Parameters.AddWithValue("$state", (int)JobState.DeadLettered);
        update.Parameters.AddWithValue("$wire", CleanupJob.WireName);
        update.Parameters.AddWithValue("$session", $"%{session.Id}%");
        await update.ExecuteNonQueryAsync();
        var watch = Host.Services.GetRequiredService<CleanupJobWatch>();

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var ended = await ReadSessionAsync(owner, session.Id);

        // Assert
        ended.CleanupStatus.ShouldBe(nameof(CleanupStatus.Failed));
        ended.CleanupFailure.ShouldBe(nameof(CleanupFailure.Interrupted));
    }
}
