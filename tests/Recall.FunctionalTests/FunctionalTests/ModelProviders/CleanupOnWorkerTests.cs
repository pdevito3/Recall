namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.Auth;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Resources.Entrypoints.Jobs;
using Shouldly;
using TestUtilities;

/// <summary>The whole path on a host with a real worker. Each test connects its own fake as the newest provider, so they run one at a time.</summary>
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
        fake.CompletionCalls.ShouldBe(1);
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
