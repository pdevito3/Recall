namespace Recall.Server.Domain.Recordings.Jobs;

using System.Diagnostics;
using BackWave.Jobs;
using Databases;
using Exceptions;
using Features;
using MediatR;
using Resources;
using Resources.Jobs;
using Resources.Storage;
using Serilog;
using Tenants;
using Transcription;

/// <summary>
/// One attempt at the text of one Recording. A kill, a stop, or a lease that returns runs this again, and it goes on from
/// the pointer that the last chunk saved (rules 4, 7, and 8 of the jobs). The default policy applies on purpose:
/// <see cref="Recording.MaxAttemptsWithoutProgress"/> ends the work before the ceiling of 10 attempts does, so a job that
/// keeps the server from living gets a true status and not a row in the dead letters.
/// </summary>
public sealed class TranscribeRecordingJob(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ITranscriptionEngine> engines,
    FileStore fileStore,
    ModelDownloads modelDownloads)
{
    public const string CloudWireName = "transcribe-recording-cloud";
    private const string TryAgainText = "The transcription provider did not answer. The job tries again.";

    private readonly ILogger _logger = Log.ForContext<TranscribeRecordingJob>();

    [Job("transcribe-recording", Queue = JobHosting.TranscriptionQueue)]
    public Task TranscribeRecordingAsync(string recordingId, string userId, string tenantId, CancellationToken cancellationToken)
        => TranscribeAsync(TranscriptionEngineKind.Local, recordingId, userId, tenantId, cancellationToken);

    /// <summary>
    /// A provider can hang for minutes, so this job runs in the queues of the provider calls, one queue for each tenant at a time
    /// (<see cref="CleanupQueues.Pick"/>), and a hung provider of one tenant holds no slot of another. A provider that did not
    /// answer leaves the Recording waiting with its audio, and the job tries again after 1, 2, 5, 10, and 15 minutes, so an outage
    /// of half an hour fails no Recording before <see cref="Recording.MaxAttemptsWithoutProgress"/> ends the tries.
    /// </summary>
    [Job(CloudWireName, Queue = CleanupQueues.First)]
    [Retry(10, 60, 120, 300, 600, 900)]
    public Task TranscribeRecordingInCloudAsync(string recordingId, string userId, string tenantId, CancellationToken cancellationToken)
        => TranscribeAsync(TranscriptionEngineKind.Cloud, recordingId, userId, tenantId, cancellationToken);

    private async Task TranscribeAsync(TranscriptionEngineKind kind, string recordingId, string userId, string tenantId, CancellationToken cancellationToken)
    {
        Activity.Current?.SetTag("app.recording_id", recordingId);
        Activity.Current?.SetTag("app.recording.engine", kind.ToString());
        try
        {
            var owner = new JournalOwner(userId, tenantId);
            await RunAsync(kind, recordingId, owner, cancellationToken);
            await SendAsync(owner, new RemoveEndedWorkAudio.Command(recordingId), cancellationToken);
        }
        catch (NotFoundException)
        {
            RemoveWorkFileLeftAfterTheDelete(recordingId, userId, tenantId);
            _logger.Here().Information("Recording {RecordingId} is gone, so its job ends", recordingId);
        }
    }

    private void RemoveWorkFileLeftAfterTheDelete(string recordingId, string userId, string tenantId)
        => RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(tenantId, userId, recordingId), recordingId, _logger);

    private async Task RunAsync(TranscriptionEngineKind kind, string recordingId, JournalOwner owner, CancellationToken cancellationToken)
    {
        var engine = engines.First(e => e.Kind == kind);
        while (true)
        {
            var start = await SendAsync(owner, new BeginTranscription.Command(recordingId, kind, engine.IsReady), cancellationToken);
            if (start is null)
            {
                if (!engine.IsReady)
                    await modelDownloads.RequestAsync(cancellationToken);
                return;
            }

            if (await RunFromAsync(engine, start, recordingId, owner, cancellationToken) == Run.Ended)
                return;
        }
    }

    private enum Run
    {
        Ended,
        ChoiceChanged,
    }

    /// <summary>
    /// The choice of a provider is read again after each part, so a part goes out only while the Admin's choice holds. When it
    /// changed, the next begin reads the new choice, or gives the Recording to the local engine.
    /// </summary>
    private async Task<Run> RunFromAsync(ITranscriptionEngine engine, BeginTranscription.Start start, string recordingId, JournalOwner owner, CancellationToken cancellationToken)
    {
        try
        {
            var input = new TranscriptionInput(start.AudioPath, start.WorkFolder, start.StartSample, start.Provider);
            var from = start.StartSample;
            await foreach (var chunk in engine.RunAsync(input, cancellationToken))
            {
                var fits = await SendAsync(owner, new SaveTranscriptChunk.Command(recordingId, engine.Kind, from, chunk.EndSample, chunk.TotalSamples, chunk.Text), cancellationToken);
                if (!fits)
                    return Run.Ended;
                from = Math.Max(from, chunk.EndSample);
                if (start.Choice is { } choice && !await SendAsync(owner, new TranscriptionChoiceHolds.Query(choice.ModelProviderId, choice.Version), cancellationToken))
                {
                    _logger.Here().Information("The provider choice changed during recording {RecordingId}, so it begins again from its saved text", recordingId);
                    return Run.ChoiceChanged;
                }
            }

            await SendAsync(owner, new CompleteTranscription.Command(recordingId), cancellationToken);
        }
        catch (TranscriptionEngineException exception) when (exception.TryAgain)
        {
            await SendAsync(owner, new WaitForTranscriptionProvider.Command(recordingId, engine.Kind), cancellationToken);
            _logger.Here().Information("Recording {RecordingId} waits for its provider, problem {Problem}", recordingId, exception.Problem);
            throw new JobSafeTextException(TryAgainText);
        }
        catch (TranscriptionEngineException exception)
        {
            await SendAsync(owner, new FailTranscription.Command(recordingId, exception.Problem), cancellationToken);
        }

        return Run.Ended;
    }

    private async Task<TResponse> SendAsync<TResponse>(JournalOwner owner, IRequest<TResponse> command, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, cancellationToken);
    }

    private async Task SendAsync(JournalOwner owner, IRequest command, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, cancellationToken);
    }

    [Job("delete-recording-audio", Queue = JobHosting.SystemQueue)]
    public async Task DeleteRecordingAudioAsync(string recordingId, string userId, string tenantId, CancellationToken cancellationToken)
    {
        Activity.Current?.SetTag("app.recording_id", recordingId);
        try
        {
            await SendAsync(new JournalOwner(userId, tenantId), new RemoveKeptAudio.Command(recordingId), cancellationToken);
        }
        catch (NotFoundException)
        {
            _logger.Here().Information("Recording {RecordingId} is gone, so its audio is too", recordingId);
        }
    }
}
