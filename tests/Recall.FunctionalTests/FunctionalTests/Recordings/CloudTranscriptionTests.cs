namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelProviders;
using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Recall.Server.Domain.ModelProviders.Dtos;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Recordings.Transcription;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Resources.Jobs;
using Recall.Server.Resources.Storage;
using Sessions;
using Shouldly;
using TestUtilities;

/// <summary>
/// The cloud engine against a local fake that speaks <c>POST /v1/audio/transcriptions</c> of OpenAI and Groq. The work audio is
/// written as the decoded WAV, so each test knows each sample that a provider must get.
/// </summary>
public class CloudTranscriptionTests : TestBase
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(60);

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private sealed class GuardedClients(ProviderLimits limits) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ProviderReadGuard(limits.ForAudio())
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ProviderAddressRule.ConnectAsync },
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static CloudTranscriptionEngine CloudEngine(long partBytes = 25_000_000, ProviderLimits? limits = null)
    {
        limits ??= new ProviderLimits();
        var options = Options.Create(new TranscriptionOptions { CloudPartBytes = partBytes });
        return new CloudTranscriptionEngine(new WorkAudio(options), new OpenAiCompatibleClient(new GuardedClients(limits), limits), options);
    }

    private static TranscribeRecordingJob JobWith(ITranscriptionEngine engine)
        => new(Services.GetRequiredService<IServiceScopeFactory>(), [engine], Services.GetRequiredService<FileStore>(), Services.GetRequiredService<ModelDownloads>());

    private static short[] Speech(int samples, params (int From, int To)[] pauses)
    {
        var audio = new short[samples];
        for (var i = 0; i < samples; i++)
            audio[i] = (short)((i * 7919 % 2000) - 1000 + (i % 3 == 0 ? 0 : 1));
        foreach (var (from, to) in pauses)
            Array.Clear(audio, from, to - from);
        return audio;
    }

    private static byte[] PcmOf(short[] samples)
    {
        var bytes = new byte[samples.Length * AudioParts.BytesPerSample];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * AudioParts.BytesPerSample), samples[i]);
        return bytes;
    }

    private static byte[] ReceivedPcm(FakeModelProvider fake)
        => fake.AudioFiles.SelectMany(file => file.Skip(AudioParts.WavHeaderBytes)).ToArray();

    private static long PartBytesFor(int samples) => AudioParts.WavHeaderBytes + ((long)samples * AudioParts.BytesPerSample);

    private async Task<CleanupScenario> ChosenAsync(ModelProviderKind kind = ModelProviderKind.OpenAiCompatible)
    {
        var scenario = await CleanupScenario.StartAsync(FactoryClient, kind, pickModel: false);
        scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
        var chose = await ChooseAsync(scenario.Client, scenario.Provider.Id);
        chose.StatusCode.ShouldBe(HttpStatusCode.OK, await chose.Content.ReadAsStringAsync());
        scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        return scenario;
    }

    private static Task<HttpResponseMessage> ChooseAsync(HttpClient client, string? providerId, string? model = FakeModelProvider.TranscriptionModelId)
        => client.PutAsJsonAsync(ApiRoutes.ModelProviders.TranscriptionEngine, new { modelProviderId = providerId, model });

    private static async Task<TranscriptionEngineDto> EngineAsync(HttpClient client)
        => (await client.GetFromJsonAsync<TranscriptionEngineDto>(ApiRoutes.ModelProviders.TranscriptionEngine)).ShouldNotBeNull();

    private static async Task<RecordingDto> RecordingWithAudioAsync(CleanupScenario scenario, short[] samples)
    {
        var session = await scenario.Client.AddSessionAsync();
        var recording = await scenario.Client.AddFinishedRecordingAsync(session.Id);
        var work = new MemoryStream(AudioParts.WavOf(PcmOf(samples)));
        await Services.GetRequiredService<FileStore>().WriteAsync(
            RecordingFiles.WorkAudio(scenario.Member.TenantId, scenario.Member.Id, recording.Id), work, CancellationToken.None);
        return recording;
    }

    private static Task RunCloudAsync(TranscribeRecordingJob job, CleanupScenario scenario, RecordingDto recording, CancellationToken cancellationToken = default)
        => job.TranscribeRecordingInCloudAsync(recording.Id, scenario.Member.Id, scenario.Member.TenantId, cancellationToken).WaitAsync(TestLimit, CancellationToken.None);

    private static async Task<string> PlainRawAsync(HttpClient client, string sessionId)
    {
        var session = await client.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(sessionId));
        return ProseMirrorToPlainText.Render(session!.RawText);
    }

    private static async Task AudioStaysAsync(HttpClient client, RecordingDto recording)
        => (await client.GetAsync(ApiRoutes.Recordings.File(recording.Id, "audio.webm"))).StatusCode.ShouldBe(HttpStatusCode.OK);

    private static Task<long> JobsOfAsync(string wireName, string recordingId, string queueLike = "%") => JournalScenario.CountOutOfBandAsync(
        "SELECT count(*) FROM backwave_jobs WHERE wire_name = $kind AND queue LIKE $queue AND IFNULL(CAST(payload AS TEXT), '') LIKE $id",
        ("$kind", wireName), ("$queue", queueLike), ("$id", $"%{recordingId}%"));

    [Test]
    [Property("Rule", "REC-17")]
    public async Task the_engine_of_the_server_sends_a_recording_to_the_chosen_provider_and_saves_its_text()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var job = ActivatorUtilities.CreateInstance<TranscribeRecordingJob>(Services);

        // Act
        await RunCloudAsync(job, scenario, recording);

        // Assert
        recording.Engine.ShouldBe(nameof(TranscriptionEngineKind.Cloud));
        (await JobsOfAsync(TranscribeRecordingJob.CloudWireName, recording.Id, "cleanup-%")).ShouldBe(1);
        (await JobsOfAsync("transcribe-recording", recording.Id)).ShouldBe(0);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldBe("Part 1 heard 1.00 seconds.");
        scenario.Fake.AudioCalls.ShouldBe(1);
        await AudioStaysAsync(scenario.Client, recording);
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_file_over_the_limit_goes_in_parts_whose_text_joins_once_in_order_with_no_gap()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = 2 * Recording.SamplesPerSecond;
        var samples = Speech(5 * Recording.SamplesPerSecond, (24_000, 24_800));
        var recording = await RecordingWithAudioAsync(scenario, samples);

        // Act
        await RunCloudAsync(JobWith(CloudEngine(PartBytesFor(partSamples))), scenario, recording);

        // Assert
        var files = scenario.Fake.AudioFiles.ToArray();
        files.Length.ShouldBe(3);
        files.ShouldAllBe(file => file.Length <= PartBytesFor(partSamples));
        (files[0].Length - AudioParts.WavHeaderBytes).ShouldBe(24_400 * AudioParts.BytesPerSample);
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        lines.Select(line => line.Split(' ')[1]).ShouldBe(["1", "2", "3"]);
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-18")]
    [Arguments(0, 1)]
    [Arguments(1, 2)]
    public async Task audio_at_the_limit_goes_in_one_part_and_one_sample_more_goes_in_two(int extraSamples, int calls)
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = 3 * Recording.SamplesPerSecond;
        scenario.Fake.Behavior.MaxAudioBytes = PartBytesFor(partSamples);
        var samples = Speech(partSamples + extraSamples);
        var recording = await RecordingWithAudioAsync(scenario, samples);

        // Act
        await RunCloudAsync(JobWith(CloudEngine(PartBytesFor(partSamples))), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(calls);
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task the_default_part_holds_two_minutes_and_fits_the_25_mb_that_openai_and_groq_take()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var maxSamples = AudioParts.MaxSamplesIn(new TranscriptionOptions().CloudPartBytes);
        var samples = Speech((int)maxSamples + 1);
        var recording = await RecordingWithAudioAsync(scenario, samples);

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        scenario.Fake.Behavior.MaxAudioBytes.ShouldBe(25_000_000);
        scenario.Fake.AudioCalls.ShouldBe(7);
        scenario.Fake.AudioFiles.ShouldAllBe(file => file.Length <= PartBytesFor(120 * Recording.SamplesPerSecond));
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-19")]
    [Arguments(401, nameof(RecordingProblem.ProviderRefused))]
    [Arguments(400, nameof(RecordingProblem.ProviderRefused))]
    [Arguments(413, nameof(RecordingProblem.ProviderRefused))]
    public async Task a_refusal_fails_the_recording_with_its_reason_and_keeps_the_audio(int status, string problem)
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = status;
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(problem);
        read.ProblemText.ShouldNotBeNullOrWhiteSpace();
        read.ProblemText!.ShouldNotContain(scenario.ApiKey);
        await AudioStaysAsync(scenario.Client, recording);
    }

    [Test]
    [Property("Rule", "REC-19")]
    public async Task an_answer_that_is_not_json_fails_the_recording_and_keeps_the_audio()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.Mode = FakeMode.BadJson;
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.ProviderBadAnswer));
        await AudioStaysAsync(scenario.Client, recording);
    }

    [Test]
    [Property("Rule", "REC-19")]
    [Arguments(429)]
    [Arguments(503)]
    [Arguments(408)]
    public async Task a_busy_provider_leaves_the_recording_waiting_and_the_next_try_goes_on(int status)
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = status;
        scenario.Fake.Behavior.AudioFaultCalls = 1;
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var job = JobWith(CloudEngine());

        // Act
        await Should.ThrowAsync<JobSafeTextException>(() => RunCloudAsync(job, scenario, recording));
        var waiting = await scenario.Client.GetRecordingAsync(recording.Id);
        await RunCloudAsync(job, scenario, recording);

        // Assert
        waiting.Status.ShouldBe(nameof(RecordingStatus.Received));
        waiting.Problem.ShouldBe(nameof(RecordingProblem.ProviderUnavailable));
        waiting.ProblemText.ShouldNotBeNullOrWhiteSpace();
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n').Length.ShouldBe(1);
        await AudioStaysAsync(scenario.Client, recording);
    }

    [Test]
    [Property("Rule", "REC-19")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_hung_provider_ends_at_its_limit_and_leaves_the_recording_waiting(bool idleLimit)
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.Mode = FakeMode.Hold;
        scenario.Fake.Behavior.DelayMs = 120_000;
        scenario.Fake.Behavior.AudioFaultCalls = 1;
        var limits = idleLimit
            ? new ProviderLimits { AudioIdle = TimeSpan.FromSeconds(1) }
            : new ProviderLimits { AudioCall = TimeSpan.FromSeconds(1) };
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var job = JobWith(CloudEngine(limits: limits));

        // Act
        await Should.ThrowAsync<JobSafeTextException>(() => RunCloudAsync(job, scenario, recording));
        var waiting = await scenario.Client.GetRecordingAsync(recording.Id);
        await RunCloudAsync(job, scenario, recording);

        // Assert
        waiting.Status.ShouldBe(nameof(RecordingStatus.Received));
        waiting.Problem.ShouldBe(nameof(RecordingProblem.ProviderUnavailable));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-19")]
    public async Task a_provider_that_never_answers_ends_failed_with_its_reason_and_not_stuck()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 503;
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var job = JobWith(CloudEngine());

        // Act
        for (var attempt = 0; attempt < Recording.MaxAttemptsWithoutProgress; attempt++)
            await Should.ThrowAsync<JobSafeTextException>(() => RunCloudAsync(job, scenario, recording));
        await RunCloudAsync(job, scenario, recording);

        // Assert
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.ProviderUnavailable));
        scenario.Fake.AudioCalls.ShouldBe(Recording.MaxAttemptsWithoutProgress);
        await AudioStaysAsync(scenario.Client, recording);
    }

    private sealed class StopAfterFirstPart(ITranscriptionEngine inner, CancellationTokenSource stop) : ITranscriptionEngine
    {
        public TranscriptionEngineKind Kind => inner.Kind;

        public bool IsReady => inner.IsReady;

        public async IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.RunAsync(input, cancellationToken))
            {
                yield return chunk;
                await stop.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    [Test]
    [Property("Rule", "REC-20")]
    public async Task a_retry_of_a_recording_whose_cloud_job_has_not_ended_queues_no_second_job()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));

        // Act
        var retry = await scenario.Client.PostAsync(ApiRoutes.Recordings.Retry(recording.Id), null);

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JobsOfAsync(TranscribeRecordingJob.CloudWireName, recording.Id)).ShouldBe(1);
    }

    private sealed class ActBeforeFirstPartIsSaved(ITranscriptionEngine inner, Func<Task> act) : ITranscriptionEngine
    {
        public TranscriptionEngineKind Kind => inner.Kind;

        public bool IsReady => inner.IsReady;

        private bool _acted;

        public async IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.RunAsync(input, cancellationToken))
            {
                if (!_acted)
                    await act();
                _acted = true;
                yield return chunk;
            }
        }
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task an_admin_who_ends_the_choice_during_a_run_stops_the_next_part_and_the_local_engine_goes_on()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = Recording.SamplesPerSecond;
        var recording = await RecordingWithAudioAsync(scenario, Speech(3 * partSamples, (12_000, 12_800), (28_000, 28_800)));
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(PartBytesFor(partSamples)), async () =>
        {
            scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
            (await ChooseAsync(scenario.Client, null, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(1);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        read.Status.ShouldBe(nameof(RecordingStatus.Received));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldStartWith("Part 1 heard");
        (await JobsOfAsync("transcribe-recording", recording.Id)).ShouldBeGreaterThan(0);
    }

    [Test]
    [Property("Rule", "REC-17")]
    public async Task a_new_choice_during_a_run_takes_the_next_part_and_the_text_joins_once()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = Recording.SamplesPerSecond;
        var samples = Speech(3 * partSamples, (12_000, 12_800), (28_000, 28_800));
        var recording = await RecordingWithAudioAsync(scenario, samples);
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(PartBytesFor(partSamples)), async () =>
        {
            scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
            (await ChooseAsync(scenario.Client, scenario.Provider.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
            scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        var parts = scenario.Fake.AudioFiles.Count;
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        lines.Select(line => line.Split(' ')[1]).ShouldBe(Enumerable.Range(1, parts).Select(part => part.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_lease_that_comes_back_between_parts_sends_and_saves_each_part_once()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = Recording.SamplesPerSecond;
        var samples = Speech(3 * partSamples, (12_000, 12_800), (28_000, 28_800));
        var recording = await RecordingWithAudioAsync(scenario, samples);
        using var stop = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(
            () => RunCloudAsync(JobWith(new StopAfterFirstPart(CloudEngine(PartBytesFor(partSamples)), stop)), scenario, recording, stop.Token));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribing));

        // Act
        await RunCloudAsync(JobWith(CloudEngine(PartBytesFor(partSamples))), scenario, recording);

        // Assert
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        var parts = scenario.Fake.AudioFiles.Count;
        parts.ShouldBeGreaterThanOrEqualTo(3);
        lines.Select(line => line.Split(' ')[1]).ShouldBe(Enumerable.Range(1, parts).Select(part => part.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task with_no_provider_that_takes_audio_the_local_engine_stays_and_says_why()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, ModelProviderKind.Anthropic);

        // Act
        var engine = await EngineAsync(scenario.Client);
        var session = await scenario.Client.AddSessionAsync();
        var recording = await scenario.Client.AddFinishedRecordingAsync(session.Id);
        scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
        var choose = await ChooseAsync(scenario.Client, scenario.Provider.Id);

        // Assert
        engine.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        engine.Reason.ShouldBe(nameof(TranscriptionEngineReason.NoProviderTakesAudio));
        engine.Text.ShouldContain("Anthropic takes no audio");
        recording.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        // A sweep of a test that runs at the same time can queue one more job of the local engine.
        (await JobsOfAsync("transcribe-recording", recording.Id)).ShouldBeGreaterThan(0);
        choose.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task a_provider_that_takes_audio_but_is_not_chosen_leaves_the_local_engine()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, ModelProviderKind.OpenAiCompatible, pickModel: false);

        // Act
        var engine = await EngineAsync(scenario.Client);

        // Assert
        engine.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        engine.Reason.ShouldBe(nameof(TranscriptionEngineReason.NoProviderChosen));
        engine.ModelProviderId.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "REC-17")]
    public async Task a_member_sees_the_chosen_provider_and_cannot_choose_one()
    {
        // Arrange
        await using var scenario = await ChosenAsync();

        // Act
        var engine = await EngineAsync(scenario.Client);
        var choose = await ChooseAsync(scenario.Client, null);

        // Assert
        engine.Engine.ShouldBe(nameof(TranscriptionEngineKind.Cloud));
        engine.ModelProviderId.ShouldBe(scenario.Provider.Id);
        engine.Model.ShouldBe(FakeModelProvider.TranscriptionModelId);
        choose.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task a_choice_that_ends_before_the_job_runs_keeps_the_audio_on_the_server()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
        (await ChooseAsync(scenario.Client, null, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        scenario.Client.SignInAsAppUser(scenario.Member.Identifier);

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(0);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        read.Status.ShouldBe(nameof(RecordingStatus.Received));
        // A sweep of a test that runs at the same time can queue one more job of the local engine.
        (await JobsOfAsync("transcribe-recording", recording.Id)).ShouldBeGreaterThan(0);
    }

    [Test]
    [Property("Rule", "REC-19")]
    public async Task a_key_that_the_server_cannot_read_fails_with_its_reason_and_sends_no_audio()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        await CleanupScenario.SetProtectedKeyAsync(scenario.Provider.Id, CleanupScenario.ProtectedKeyUnderAnotherRing());
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(0);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.ProviderKeyUnreadable));
        await AudioStaysAsync(scenario.Client, recording);
    }

    private sealed class LocalEngineThatMustNotRun : ITranscriptionEngine
    {
        public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

        public bool IsReady => true;

        public IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A local job must not run on a Recording of the cloud engine.");
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task a_job_of_the_other_engine_leaves_the_recording_alone()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var local = JobWith(new LocalEngineThatMustNotRun());

        // Act
        await local.TranscribeRecordingAsync(recording.Id, scenario.Member.Id, scenario.Member.TenantId, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Received));
        read.Engine.ShouldBe(nameof(TranscriptionEngineKind.Cloud));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "REC-22")]
    public async Task the_job_rows_hold_no_text_and_no_key_of_a_cloud_transcription()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const string marker = "R11B-MARKER-heard-words";
        scenario.Fake.Behavior.Reply = marker;
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldBe(marker);
        var jobs = await CleanupScenario.DumpOfTableAsync("backwave_jobs");
        jobs.ShouldContain(recording.Id);
        jobs.ShouldNotContain(marker);
        jobs.ShouldNotContain(scenario.ApiKey);
        (await CleanupScenario.DumpOfTableAsync("recordings")).ShouldNotContain(marker);
    }

    [Test]
    [Property("Rule", "REC-19")]
    public void a_provider_that_is_down_for_half_an_hour_fails_no_recording()
    {
        // Arrange
        var registration = Services.GetRequiredService<BackWave.Jobs.JobRegistry>().Registrations.Single(r => r.WireName == TranscribeRecordingJob.CloudWireName);

        // Act
        var waits = registration.Retry.ShouldNotBeNull().BackoffByAttempt.Take(Recording.MaxAttemptsWithoutProgress).ToArray();

        // Assert
        waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    private static double HeardSeconds(string line) => double.Parse(line.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_model_with_an_output_limit_of_2000_tokens_hears_each_word_of_13_minutes_of_fast_speech()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.AudioOutputTokens = 2_000;
        var samples = Speech((int)AudioParts.MaxSamplesIn(new TranscriptionOptions().CloudPartBytes) + 1);
        var recording = await RecordingWithAudioAsync(scenario, samples);

        // Act
        await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);

        // Assert
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        lines.ShouldAllBe(line => line.EndsWith(" seconds.") && !line.Contains(" of "));
        lines.Sum(HeardSeconds).ShouldBe((double)samples.Length / Recording.SamplesPerSecond, lines.Length * 0.01);
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_cut_answer_is_never_saved_and_its_part_goes_again_in_two()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int outputTokens = (int)(10 * FakeModelProvider.TokensPerSecond);
        scenario.Fake.Behavior.AudioOutputTokens = outputTokens;
        var samples = Speech(30 * Recording.SamplesPerSecond);
        var recording = await RecordingWithAudioAsync(scenario, samples);
        var engine = CloudEngine(PartBytesFor(16 * Recording.SamplesPerSecond), new ProviderLimits { AudioOutputTokens = outputTokens });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        lines.ShouldAllBe(line => line.EndsWith(" seconds.") && !line.Contains(" of "));
        lines.Sum(HeardSeconds).ShouldBe(30, lines.Length * 0.01);
        scenario.Fake.AudioCalls.ShouldBeGreaterThan(lines.Length);
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-19")]
    public async Task a_cut_answer_of_a_short_part_fails_the_recording_with_its_reason_and_keeps_the_audio()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        scenario.Fake.Behavior.AudioOutputTokens = 1;
        var recording = await RecordingWithAudioAsync(scenario, Speech(5 * Recording.SamplesPerSecond));

        // Act
        await RunCloudAsync(JobWith(CloudEngine(limits: new ProviderLimits { AudioOutputTokens = 1 })), scenario, recording);

        // Assert
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.ProviderBadAnswer));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldBeEmpty();
        await AudioStaysAsync(scenario.Client, recording);
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_quiet_end_of_the_window_leaves_a_last_part_of_at_least_one_second()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = 10 * Recording.SamplesPerSecond;
        var samples = Speech(partSamples + 400, (partSamples - AudioParts.FrameSamples, partSamples));
        var recording = await RecordingWithAudioAsync(scenario, samples);

        // Act
        await RunCloudAsync(JobWith(CloudEngine(PartBytesFor(partSamples))), scenario, recording);

        // Assert
        var files = scenario.Fake.AudioFiles.ToArray();
        files.Length.ShouldBe(2);
        files.ShouldAllBe(file => file.Length >= PartBytesFor(Recording.SamplesPerSecond) && file.Length <= PartBytesFor(partSamples));
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-17")]
    public async Task a_new_model_of_the_same_provider_during_a_run_takes_the_next_part()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = Recording.SamplesPerSecond;
        const string newModel = "fake-transcribe-2";
        var samples = Speech(3 * partSamples, (12_000, 12_800), (28_000, 28_800));
        var recording = await RecordingWithAudioAsync(scenario, samples);
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(PartBytesFor(partSamples)), async () =>
        {
            scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
            (await ChooseAsync(scenario.Client, scenario.Provider.Id, newModel)).StatusCode.ShouldBe(HttpStatusCode.OK);
            scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        var models = scenario.Fake.AudioModels.ToArray();
        models[0].ShouldBe(FakeModelProvider.TranscriptionModelId);
        models.Skip(1).ShouldAllBe(model => model == newModel);
        models.Length.ShouldBeGreaterThan(1);
        ReceivedPcm(scenario.Fake).ShouldBe(PcmOf(samples));
        (await scenario.Client.GetRecordingAsync(recording.Id)).Status.ShouldBe(nameof(RecordingStatus.Transcribed));
    }

    [Test]
    [Property("Rule", "REC-17")]
    public async Task a_new_key_of_the_same_provider_during_a_run_takes_the_next_part()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        const int partSamples = Recording.SamplesPerSecond;
        var recording = await RecordingWithAudioAsync(scenario, Speech(3 * partSamples, (12_000, 12_800), (28_000, 28_800)));
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(PartBytesFor(partSamples)), async () =>
        {
            scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
            var list = (await scenario.Client.GetFromJsonAsync<ModelProviderListDto>(ApiRoutes.ModelProviders.Root)).ShouldNotBeNull();
            var provider = list.Items.Single(p => p.Id == scenario.Provider.Id);
            var updated = await scenario.Client.PutAsJsonAsync(ApiRoutes.ModelProviders.GetRecord(provider.Id), new
            {
                name = provider.Name,
                baseUrl = provider.BaseUrl,
                apiKey = CleanupScenario.NewKey(),
                model = provider.Model,
                version = provider.Version,
            });
            updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
            scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(2);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Status.ShouldBe(nameof(RecordingStatus.Failed));
        read.Problem.ShouldBe(nameof(RecordingProblem.ProviderRefused));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldStartWith("Part 1 heard");
    }

    [Test]
    [Property("Rule", "REC-21")]
    public async Task a_part_that_arrives_after_the_hand_over_to_the_local_engine_is_not_saved()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var recording = await RecordingWithAudioAsync(scenario, Speech(Recording.SamplesPerSecond));
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(), async () =>
        {
            scenario.Client.SignInAsAppUser(scenario.Admin.Identifier);
            (await ChooseAsync(scenario.Client, null, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            scenario.Client.SignInAsAppUser(scenario.Member.Identifier);
            await RunCloudAsync(JobWith(CloudEngine()), scenario, recording);
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(1);
        var read = await scenario.Client.GetRecordingAsync(recording.Id);
        read.Engine.ShouldBe(nameof(TranscriptionEngineKind.Local));
        read.Status.ShouldBe(nameof(RecordingStatus.Received));
        (await PlainRawAsync(scenario.Client, recording.SessionId)).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "REC-18")]
    public async Task a_part_of_a_second_run_with_other_cuts_joins_only_where_the_saved_text_ends()
    {
        // Arrange
        await using var scenario = await ChosenAsync();
        var samples = Speech(4 * Recording.SamplesPerSecond, (20_000, 20_800), (44_000, 44_800));
        var recording = await RecordingWithAudioAsync(scenario, samples);
        var engine = new ActBeforeFirstPartIsSaved(CloudEngine(PartBytesFor(3 * Recording.SamplesPerSecond)), async () =>
        {
            using var stop = new CancellationTokenSource();
            var second = JobWith(new StopAfterFirstPart(CloudEngine(PartBytesFor(2 * Recording.SamplesPerSecond)), stop));
            await Should.ThrowAsync<OperationCanceledException>(() => RunCloudAsync(second, scenario, recording, stop.Token));
        });

        // Act
        await RunCloudAsync(JobWith(engine), scenario, recording);

        // Assert
        scenario.Fake.AudioCalls.ShouldBe(2);
        var lines = (await PlainRawAsync(scenario.Client, recording.SessionId)).Split('\n');
        lines.Length.ShouldBe(1);
        lines[0].ShouldStartWith("Part 2 heard");
    }
}
