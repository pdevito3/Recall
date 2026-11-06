namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

[NotInParallel(nameof(DownloadModelJobTests))]
public class DownloadModelJobTests : TestBase
{
    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("recall_model_").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);

        public (DownloadModelJob Job, ModelStore Store) Build(IHttpClientFactory client)
        {
            var settings = Options.Create(new TranscriptionOptions { ModelRoot = Root });
            var store = new ModelStore(settings);
            var scopes = Services.GetRequiredService<IServiceScopeFactory>();
            var clock = TimeProvider.System;
            return (new DownloadModelJob(client, store, new ModelDownloads(scopes, store, clock), new RecordingSweep(scopes, Services.GetRequiredService<Recall.Server.Resources.Storage.FileStore>(), clock), settings, clock), store);
        }
    }

    private static byte[] Bytes(int length) => Enumerable.Repeat((byte)7, length).ToArray();

    [Test]
    [Property("Rule", "REC-9")]
    public async Task a_file_that_fails_its_hash_is_rejected_and_the_model_is_not_ready()
    {
        // Arrange
        using var rig = new Rig();
        var tokens = ModelManifest.Files[0];
        using var answer = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes((int)tokens.ByteLength)) });
        var (job, store) = rig.Build(answer);

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        store.Status.ShouldBe(ModelStatus.HashRejected);
        store.IsReady.ShouldBeFalse();
        File.Exists(store.PathOf(tokens)).ShouldBeFalse();
        File.Exists(store.PathOf(tokens) + ".part").ShouldBeFalse();
        File.Exists(Path.Combine(store.Folder, ".verified")).ShouldBeFalse();
        answer.Requests.ShouldHaveSingleItem();
    }

    [Test]
    [Property("Rule", "REC-9")]
    public async Task a_file_longer_than_the_manifest_says_stops_the_download()
    {
        // Arrange
        using var rig = new Rig();
        var tokens = ModelManifest.Files[0];
        using var answer = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes((int)tokens.ByteLength + 1)) });
        var (job, store) = rig.Build(answer);

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        store.Status.ShouldBe(ModelStatus.HashRejected);
        store.IsReady.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "REC-12")]
    public async Task no_network_ends_as_a_failed_download_with_a_retry_and_no_throw()
    {
        // Arrange
        using var rig = new Rig();
        using var answer = new Answering(_ => throw new HttpRequestException("Connection refused"));
        var (job, store) = rig.Build(answer);
        var before = await RecordingSweepTests.CountJobsAsync("download-model", "");

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        store.Status.ShouldBe(ModelStatus.DownloadFailed);
        (await RecordingSweepTests.CountJobsAsync("download-model", "")).ShouldBe(before + 1);
    }

    [Test]
    [Property("Rule", "REC-12")]
    public async Task a_timeout_of_the_resilience_handler_ends_as_a_failed_download_with_a_retry()
    {
        // Arrange
        using var rig = new Rig();
        using var answer = new Answering(_ => throw new TimeoutRejectedException());
        var (job, store) = rig.Build(answer);
        var before = await RecordingSweepTests.CountJobsAsync("download-model", "");

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        store.Status.ShouldBe(ModelStatus.DownloadFailed);
        (await RecordingSweepTests.CountJobsAsync("download-model", "")).ShouldBe(before + 1);
    }

    [Test]
    [Property("Rule", "REC-12")]
    public async Task a_retry_that_cannot_be_queued_leaves_the_next_attempt_free_to_run()
    {
        // Arrange
        using var rig = new Rig();
        using var answer = new Answering(_ => throw new HttpRequestException("Connection refused"));
        var settings = Options.Create(new TranscriptionOptions { ModelRoot = rig.Root });
        var store = new ModelStore(settings);
        var scopes = Services.GetRequiredService<IServiceScopeFactory>();
        var job = new DownloadModelJob(answer, store, new ModelDownloads(new NoScopes(), store, TimeProvider.System),
            new RecordingSweep(scopes, Services.GetRequiredService<Recall.Server.Resources.Storage.FileStore>(), TimeProvider.System), settings, TimeProvider.System);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => job.DownloadModelAsync(CancellationToken.None));

        // Assert
        store.TryReserveDownload().ShouldBeTrue();
        store.TryBeginDownload(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)).ShouldBeTrue();
    }

    private sealed class NoScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The database is not there.");
    }

    [Test]
    [Property("Rule", "REC-12")]
    public async Task an_error_from_the_server_of_the_model_is_a_failed_download()
    {
        // Arrange
        using var rig = new Rig();
        using var answer = new Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var (job, store) = rig.Build(answer);

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        store.Status.ShouldBe(ModelStatus.DownloadFailed);
    }

    [Test]
    [Property("Rule", "REC-9")]
    public async Task each_request_of_a_download_is_a_get_for_a_file_at_the_pinned_commit()
    {
        // Arrange
        using var rig = new Rig();
        using var answer = new Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var (job, _) = rig.Build(answer);

        // Act
        await job.DownloadModelAsync(CancellationToken.None);

        // Assert
        var request = answer.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Content.ShouldBeNull();
        request.RequestUri!.ToString().ShouldBe($"{ModelManifest.BaseUrl}/{ModelManifest.Files[0].Name}");
    }

    [Test]
    [Property("Rule", "REC-9")]
    public void each_file_of_the_manifest_has_a_sha_256()
    {
        // Arrange
        var files = ModelManifest.Files;

        // Act
        var lengths = files.Select(f => f.Sha256.Length).Distinct();

        // Assert
        lengths.ShouldBe([64]);
        ModelManifest.BaseUrl.ShouldContain("1ab9323565ddb038682214b292f588070a538ce2");
    }

    [Test]
    public void the_second_download_request_queues_nothing_while_the_first_waits()
    {
        // Arrange
        using var rig = new Rig();
        var (_, store) = rig.Build(new Answering(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        // Act
        var first = store.TryReserveDownload();
        var second = store.TryReserveDownload();

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse();
    }
}
