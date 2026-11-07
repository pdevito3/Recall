namespace Recall.FunctionalTests.FunctionalTests.Support;

using System.Net;
using System.Net.Http.Json;
using BackWave.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.Auth;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Dtos;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Resources.Jobs;
using Shouldly;
using TestUtilities;

/// <summary>A provider can echo the words and the key of a request. This runs each failure, and searches each place that keeps text.</summary>
public partial class SupportBundleTests
{
    private static async Task<ModelProviderDto> ConnectAsync(AuthBrowser owner, ModelProviderKind kind, string baseUrl, string apiKey)
    {
        using var added = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.ModelProviders.Root, new { kind = kind.ToString(), name = "Mine", baseUrl, apiKey });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var provider = (await added.Content.ReadFromJsonAsync<ModelProviderDto>()).ShouldNotBeNull();
        using var picked = await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.ModelProviders.GetRecord(provider.Id), new
        {
            name = provider.Name,
            baseUrl = provider.BaseUrl,
            model = FakeModelProvider.ModelId,
            makeActive = true,
            version = provider.Version,
        });
        picked.StatusCode.ShouldBe(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());
        return provider;
    }

    private static async Task<string> RunCleanupOfNewSessionAsync(AuthBrowser owner, string marker, Func<Exception?, bool> expected)
    {
        var session = await AddSessionAsync(owner);
        using var saved = await SaveDraftAsync(owner, ApiRoutes.Sessions.Draft(session.Id), $"raw {marker}", 0, 1);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var started = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Cleanup(session.Id), new { });
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());

        var row = await Host.WithDatabaseAsync(database => database.Sessions.IgnoreQueryFilters()
            .Where(s => s.Id == session.Id)
            .Select(s => new { s.UserId, s.TenantId, s.CleanupRunId })
            .SingleAsync());
        var job = ActivatorUtilities.CreateInstance<CleanupJob>(Host.Services);
        Exception? thrown = null;
        await Host.InRequestFlowAsync(async () =>
        {
            try
            {
                await job.CleanupSessionAsync(session.Id, row.UserId, row.TenantId, row.CleanupRunId!, new JobContext { JobId = Guid.NewGuid(), Attempt = 1 }, CancellationToken.None);
            }
            catch (JobSafeTextException fault)
            {
                thrown = fault;
                thrown.Message.ShouldNotContain(marker);
            }
        });
        expected(thrown).ShouldBeTrue();
        using var read = await owner.GetAsync($"/{ApiRoutes.Sessions.GetRecord(session.Id)}", csrf: true);
        var body = await read.Content.ReadAsStringAsync();
        body.ShouldNotContain("Cannot read this input");
        body.ShouldNotContain("Stopped at");
        return row.CleanupRunId!;
    }

    [Test]
    [Property("Rule", "SES-39")]
    [Property("Rule", "SES-19")]
    public async Task an_echo_of_the_words_and_the_key_by_a_provider_reaches_no_log_no_span_no_file_and_no_bundle()
    {
        // Arrange
        using var owner = await OwnerAsync();
        var marker = NewMarker();
        var key = $"fake-key-{Guid.NewGuid():N}";
        await using var fake = await FakeModelProvider.StartAsync(key);

        var runs = new List<string>();

        // Act
        foreach (var kind in new[] { ModelProviderKind.Anthropic, ModelProviderKind.OpenAiCompatible })
        {
            await ConnectAsync(owner, kind, kind == ModelProviderKind.Anthropic ? fake.AnthropicBaseUrl : fake.CompatibleBaseUrl, key);

            fake.Behavior.Mode = FakeMode.EchoError;
            runs.Add(await RunCleanupOfNewSessionAsync(owner, marker, thrown => thrown is null));

            fake.Behavior.Mode = FakeMode.Status;
            fake.Behavior.Status = 503;
            runs.Add(await RunCleanupOfNewSessionAsync(owner, marker, thrown => thrown is JobSafeTextException));

            fake.Behavior.Mode = FakeMode.BadJson;
            fake.Behavior.Reply = $"not an object {marker} {key}";
            runs.Add(await RunCleanupOfNewSessionAsync(owner, marker, thrown => thrown is null));
            fake.Behavior.Reply = null;

            fake.Behavior.Mode = FakeMode.Truncate;
            runs.Add(await RunCleanupOfNewSessionAsync(owner, marker, thrown => thrown is null));
        }

        fake.Behavior.Mode = FakeMode.MidStreamError;
        await ConnectAsync(owner, ModelProviderKind.Anthropic, fake.AnthropicBaseUrl, key);
        runs.Add(await RunCleanupOfNewSessionAsync(owner, marker, thrown => thrown is JobSafeTextException));

        var last = await AddSessionAsync(owner);
        await WaitUntilEachSinkHoldsAsync(last.Id);
        var entries = await EntriesOfBundleAsync(owner);

        // Assert
        fake.CompletionCalls.ShouldBeGreaterThanOrEqualTo(9);
        var (logs, _) = FilesOfHost();
        foreach (var run in runs)
        {
            logs.Values.Any(content => content.Contains(run, StringComparison.Ordinal)).ShouldBeTrue("The job line of each run reaches the search, or the search proves nothing.");
            AuthHosts.BundleExport.LogsHold(run).ShouldBeTrue();
        }

        ShouldHoldNoMarker(marker, entries);
        ShouldHoldNoMarker(key, entries);
    }
}
