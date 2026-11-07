namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using BackWave.Jobs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests.FunctionalTests.Recordings;
using Recall.Server.Databases;
using Recall.Server.Domain.ModelProviders.Clients;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Cleanup;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Domain.Tenants;
using Shouldly;
using TestUtilities;

/// <summary>
/// A Cleanup and a transcript chunk can write to one Session in each order. Raw stays the source: each chunk stays in Raw,
/// no Cleanup overwrites it, and a Cleanup of an older Raw leaves the Session Stale, so the person sees that words are missing.
/// </summary>
public class CleanupAndChunkOrderTests : TestBase
{
    private const string FirstWords = "i went to teh market";
    private const string ChunkWords = "and then a later chunk came";
    private const long ChunkEnd = 16_000;

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private sealed class ChunkDuringTheCall(IModelProviderClient inner, Func<Task> saveChunk) : IModelProviderClient
    {
        public Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
            => inner.ListModelsAsync(endpoint, cancellationToken);

        public async Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        {
            await saveChunk();
            return await inner.CompleteAsync(endpoint, model, instructions, text, cancellationToken);
        }
    }

    private static async Task SaveChunkAsync(CleanupScenario scenario, string sessionId, string recordingId)
    {
        var owner = new JournalOwner(scenario.Member.Id, await CleanupScenario.TenantIdOfAsync(sessionId));
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateJournalScope(owner);
        var fits = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SaveTranscriptChunk.Command(recordingId, Recall.Server.Domain.Recordings.TranscriptionEngineKind.Local, 0, ChunkEnd, ChunkEnd, ChunkWords));
        fits.ShouldBeTrue();
    }

    private static string PlainOf(string draft) => ProseMirrorToPlainText.Render(draft);

    [Test]
    public async Task a_chunk_during_the_call_stays_in_raw_and_the_cleanup_of_the_older_raw_is_stale()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync(FirstWords);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        var started = await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var read = await scenario.ReadAsync(session.Id);
        var client = new ChunkDuringTheCall(Services.GetRequiredService<IModelProviderClient>(), () => SaveChunkAsync(scenario, session.Id, recording.Id));
        var job = ActivatorUtilities.CreateInstance<CleanupJob>(Services, client);

        // Act
        await job.CleanupSessionAsync(
            session.Id,
            scenario.Member.Id,
            await CleanupScenario.TenantIdOfAsync(session.Id),
            await CleanupScenario.RunIdOfAsync(session.Id),
            new JobContext { JobId = Guid.NewGuid(), Attempt = 1 },
            CancellationToken.None);
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        PlainOf(after.RawText).ShouldContain(FirstWords);
        PlainOf(after.RawText).ShouldContain(ChunkWords);
        PlainOf(after.CleanedText).ShouldContain("(cleaned)");
        PlainOf(after.CleanedText).ShouldNotContain(ChunkWords);
        after.LastCleanupRawRevision.ShouldBe(read.RawLatestRevision);
        after.RawLatestRevision.ShouldBe(read.RawLatestRevision + 1);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Stale));
    }

    [Test]
    public async Task a_new_cleanup_after_a_late_chunk_reads_the_chunk_and_is_clean()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithRunningCleanupAsync(FirstWords);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await scenario.RunJobAsync(session.Id);
        await SaveChunkAsync(scenario, session.Id, recording.Id);

        // Act
        (await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await scenario.RunJobAsync(session.Id);
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        PlainOf(after.CleanedText).ShouldContain(ChunkWords);
        after.LastCleanupRawRevision.ShouldBe(after.RawLatestRevision);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
    }

    [Test]
    public async Task a_chunk_after_the_result_keeps_cleaned_and_makes_it_stale()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithRunningCleanupAsync(FirstWords);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        await scenario.RunJobAsync(session.Id);
        var before = await scenario.ReadAsync(session.Id);
        before.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));

        // Act
        await SaveChunkAsync(scenario, session.Id, recording.Id);
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        PlainOf(after.RawText).ShouldContain(ChunkWords);
        after.CleanedText.ShouldBe(before.CleanedText);
        after.CleanedLatestRevision.ShouldBe(before.CleanedLatestRevision);
        after.LastCleanupRawRevision.ShouldBe(before.LastCleanupRawRevision);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Stale));
    }

    [Test]
    public async Task a_chunk_before_the_claim_is_in_the_words_of_the_run_and_the_result_is_clean()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithRunningCleanupAsync(FirstWords);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);

        // Act
        await SaveChunkAsync(scenario, session.Id, recording.Id);
        await scenario.RunJobAsync(session.Id);
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        scenario.Fake.RequestBodies.ShouldContain(body => body.Contains(ChunkWords, StringComparison.Ordinal));
        PlainOf(after.CleanedText).ShouldContain(ChunkWords);
        after.LastCleanupRawRevision.ShouldBe(after.RawLatestRevision);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
    }

    [Test]
    public async Task a_device_result_on_a_raw_before_a_chunk_keeps_the_chunk_and_is_stale()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync(FirstWords);
        var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
        var oldRevision = (await scenario.ReadAsync(session.Id)).RawLatestRevision;
        await SaveChunkAsync(scenario, session.Id, recording.Id);

        // Act
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Clean words of the device.", baseCleanedRevision = 0, baseRawRevision = oldRevision });
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        PlainOf(after.RawText).ShouldContain(ChunkWords);
        PlainOf(after.CleanedText).ShouldBe("Clean words of the device.");
        after.LastCleanupRawRevision.ShouldBe(oldRevision);
        after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Stale));
    }

    [Test]
    public async Task a_chunk_and_the_end_of_a_run_at_one_time_both_stay()
    {
        // Arrange
        const int rounds = 8;
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var tenantId = default(string);
        var owner = default(JournalOwner);
        var factory = Services.GetRequiredService<IServiceScopeFactory>();

        for (var round = 0; round < rounds; round++)
        {
            var session = await scenario.SessionWithRunningCleanupAsync($"{FirstWords} {round}");
            var recording = await FactoryClient.AddFinishedRecordingAsync(session.Id);
            tenantId ??= await CleanupScenario.TenantIdOfAsync(session.Id);
            owner ??= new JournalOwner(scenario.Member.Id, tenantId);
            var runId = await CleanupScenario.RunIdOfAsync(session.Id);
            var read = await scenario.ReadAsync(session.Id);
            var output = CleanupOutput.Checked("The clean words.", "A synopsis.", [], [], []).ShouldNotBeNull();

            // Act
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    await using var scope = factory.CreateJournalScope(owner);
                    await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CompleteCleanupRun.Command(session.Id, runId, read.RawLatestRevision, output));
                }),
                Task.Run(async () =>
                {
                    await using var scope = factory.CreateJournalScope(owner);
                    await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SaveTranscriptChunk.Command(recording.Id, Recall.Server.Domain.Recordings.TranscriptionEngineKind.Local, 0, ChunkEnd, ChunkEnd, ChunkWords));
                }));
            var after = await scenario.ReadAsync(session.Id);

            // Assert
            PlainOf(after.RawText).ShouldContain(ChunkWords);
            PlainOf(after.CleanedText).ShouldBe("The clean words.");
            after.LastCleanupRawRevision.ShouldBe(read.RawLatestRevision);
            after.RawLatestRevision.ShouldBe(read.RawLatestRevision + 1);
            after.CleanupStatus.ShouldBe(nameof(CleanupStatus.Stale));
        }
    }
}
