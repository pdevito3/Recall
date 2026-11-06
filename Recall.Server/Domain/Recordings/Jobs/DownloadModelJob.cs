namespace Recall.Server.Domain.Recordings.Jobs;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BackWave.Jobs;
using Databases;
using Microsoft.Extensions.Options;
using Resources;
using Resources.Jobs;
using Serilog;
using Tenants;
using Transcription;

/// <summary>Queues the one download that many waiting Recordings need.</summary>
public sealed class ModelDownloads(IServiceScopeFactory scopeFactory, ModelStore modelStore, TimeProvider timeProvider)
{
    public async Task RequestAsync(CancellationToken cancellationToken)
    {
        if (modelStore.TryReserveDownload())
            await EnqueueAsync(timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task EnqueueAsync(DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveAndEnqueueAsync(
            (jobs, ct) => jobs.EnqueueAsync(new DownloadModel(), dueTime, ct), cancellationToken);
    }
}

/// <summary>
/// Gets the model on first use, with the pinned hash of each file as the only test of what it got. It sends nothing of a
/// person: each request is a GET for a file of the manifest. The job shares the one transcription worker, because no
/// transcription can run without the model.
/// </summary>
public sealed class DownloadModelJob(
    IHttpClientFactory httpClientFactory,
    ModelStore modelStore,
    ModelDownloads modelDownloads,
    RecordingSweep recordingSweep,
    IOptions<TranscriptionOptions> options,
    TimeProvider timeProvider)
{
    public const string HttpClientName = "speech-model";

    private static readonly TimeSpan ReadStallLimit = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger = Log.ForContext<DownloadModelJob>();

    [Job("download-model", Queue = JobHosting.TranscriptionQueue)]
    public async Task DownloadModelAsync(CancellationToken cancellationToken)
    {
        var retryGap = TimeSpan.FromMinutes(options.Value.ModelRetryMinutes);
        if (!modelStore.IsReady && !modelStore.TryBeginDownload(timeProvider.GetUtcNow(), retryGap))
            return;

        try
        {
            if (!modelStore.IsReady)
                await GetEachFileAsync(cancellationToken);
            modelStore.EndDownload(ModelStatus.Missing, retryIsQueued: false);
            await recordingSweep.QueueWaitingRecordingsAsync(cancellationToken);
            _logger.Here().Information("The speech model is ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            modelStore.EndDownload(ModelStatus.Missing, retryIsQueued: true);
            throw;
        }
        catch (ModelHashRejectedException)
        {
            await EndWithFaultAsync(ModelStatus.HashRejected, nameof(ModelHashRejectedException), retryGap, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await EndWithFaultAsync(ModelStatus.DownloadFailed, exception.GetType().Name, retryGap, cancellationToken);
        }
    }

    private async Task EndWithFaultAsync(ModelStatus fault, string faultType, TimeSpan retryGap, CancellationToken cancellationToken)
    {
        Activity.Current?.SetTag("app.model.fault", faultType);
        _logger.Here().Warning("The speech model download ended with {ModelStatus}, because of {FaultType}", fault, faultType);
        modelStore.EndDownload(fault, retryIsQueued: true);
        await modelDownloads.EnqueueAsync(timeProvider.GetUtcNow() + retryGap, cancellationToken);
    }

    private async Task GetEachFileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(modelStore.Folder);
        var client = httpClientFactory.CreateClient(HttpClientName);
        foreach (var file in ModelManifest.Files)
            await GetFileAsync(client, file, cancellationToken);

        modelStore.MarkVerified();
    }

    private async Task GetFileAsync(HttpClient client, ModelFile file, CancellationToken cancellationToken)
    {
        var target = modelStore.PathOf(file);
        if (File.Exists(target) && await HasPinnedHashAsync(target, file, cancellationToken))
            return;

        var partial = target + ".part";
        await FetchAsync(client, file, partial, cancellationToken);
        if (!await HasPinnedHashAsync(partial, file, cancellationToken))
        {
            File.Delete(partial);
            throw new ModelHashRejectedException();
        }

        File.Move(partial, target, overwrite: true);
    }

    // A file that a stop cut goes on from its last byte, because the largest file takes minutes on a slow line.
    private async Task FetchAsync(HttpClient client, ModelFile file, string partial, CancellationToken cancellationToken)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have >= file.ByteLength)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Value.ModelBaseUrl.TrimEnd('/')}/{file.Name}");
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var resumes = response.StatusCode == HttpStatusCode.PartialContent;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partial, resumes ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        var written = resumes ? have : 0;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(ReadStallLimit);
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
                break;

            written += read;
            if (written > file.ByteLength)
                throw new ModelHashRejectedException();

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        output.Flush(flushToDisk: true);
    }

    private static async Task<bool> HasPinnedHashAsync(string path, ModelFile file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash) == file.Sha256;
    }
}

public sealed class ModelHashRejectedException() : Exception("A file of the speech model did not pass its hash check.");
