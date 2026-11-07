namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Jobs;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using ModelProviders.Clients;
using Resources;
using Resources.Storage;
using Serilog;
using Transcription;

public static class BeginTranscription
{
    public sealed record Start(string AudioPath, string WorkFolder, long StartSample, AudioProvider? Provider);

    /// <summary>A job of another engine does nothing, so two engines never work on one Recording at a time.</summary>
    public sealed record Command(string Id, TranscriptionEngineKind Engine, bool EngineIsReady) : IRequest<Start?>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore, ApiKeyProtector keys, TimeProvider timeProvider) : IRequestHandler<Command, Start?>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Start?> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.begin")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription || recording.Engine != request.Engine)
                return null;

            RecordingWork.TryDeleteSlices(fileStore, recording, _logger);
            AudioProvider? provider = null;
            if (recording.Engine == TranscriptionEngineKind.Cloud)
            {
                var chosen = await dbContext.ModelProviders.AsNoTracking()
                    .TagWithOperationCallSite("recordings.transcribe.provider")
                    .TranscriptionFirst()
                    .FirstOrDefaultAsync(cancellationToken);
                if (chosen is null)
                {
                    await HandToLocalEngineAsync(recording, cancellationToken);
                    return null;
                }

                var read = keys.Read(chosen.ProtectedApiKey);
                provider = read is ProtectedKeyRead.Unreadable
                    ? null
                    : new AudioProvider(new ModelEndpoint(chosen.Kind, chosen.BaseUrl, (read as ProtectedKeyRead.Readable)?.ApiKey), chosen.TranscriptionModel!);
            }

            Start? start = null;
            if (!request.EngineIsReady)
            {
                recording.WaitForModel();
            }
            else if (recording.Engine == TranscriptionEngineKind.Cloud && provider is null)
            {
                recording.Fail(RecordingProblem.ProviderKeyUnreadable);
            }
            else if (!fileStore.Exists(RecordingFiles.Audio(recording)))
            {
                recording.Fail(RecordingProblem.AudioMissing);
            }
            else if (recording.BeginAttempt())
            {
                start = new Start(
                    fileStore.LocalPathOf(RecordingFiles.Audio(recording)),
                    fileStore.LocalPathOf(RecordingFiles.Folder(recording)),
                    recording.TranscribedSamples,
                    provider);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Recording {RecordingId} is {Status}, {Problem}", recording.Id, recording.Status, recording.Problem);
            return start;
        }

        // The Admin took the choice of a provider back after this Recording was queued. The audio does not leave the server
        // with no choice in force, so the local engine makes the text from the saved pointer on.
        private async Task HandToLocalEngineAsync(Recording recording, CancellationToken cancellationToken)
        {
            recording.HandToLocalEngine();
            var now = timeProvider.GetUtcNow();
            await dbContext.SaveAndEnqueueAsync(
                (jobs, ct) => RecordingWork.EnqueueAsync(jobs, recording, cloudQueue: null, now, ct),
                cancellationToken);
            _logger.Here().Information("Recording {RecordingId} has no provider for audio, so the local engine makes its text", recording.Id);
        }
    }
}

public static class WaitForTranscriptionProvider
{
    public sealed record Command(string Id, TranscriptionEngineKind Engine) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.wait_for_provider")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription || recording.Engine != request.Engine)
                return;

            recording.WaitForProvider();
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Recording {RecordingId} waits for its provider", recording.Id);
        }
    }
}

public static class SaveTranscriptChunk
{
    /// <summary>A chunk of an engine that no longer has the Recording is refused, so a hand-over repeats no text.</summary>
    public sealed record Command(string Id, TranscriptionEngineKind Engine, long EndSample, long TotalSamples, string Words) : IRequest<bool>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, bool>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.chunk")
                .GetById(request.Id, cancellationToken);
            if (recording.Engine != request.Engine)
                return false;

            var words = request.Words.Trim();
            if (!StorableText.CanStore(words))
            {
                recording.Fail(RecordingProblem.EngineCrashed);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.Here().Warning("The engine gave text that cannot be stored for recording {RecordingId}", recording.Id);
                return false;
            }

            var hasWords = words.Length > 0;
            if (!recording.AdvanceTo(request.EndSample, request.TotalSamples, hasWords))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            var fits = true;
            if (hasWords)
            {
                var session = await dbContext.Sessions
                    .TagWithOperationCallSite("recordings.transcribe.session")
                    .GetById(recording.SessionId, cancellationToken);
                fits = session.TryAppendTranscript(words, timeProvider.GetUtcNow());
                if (!fits)
                {
                    dbContext.DiscardLostRace();
                    return await FailForLengthAsync(request.Id, cancellationToken);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Saved a chunk of recording {RecordingId} up to sample {EndSample}", recording.Id, request.EndSample);
            return fits;
        }

        private async Task<bool> FailForLengthAsync(string id, CancellationToken cancellationToken)
        {
            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.too_long")
                .GetById(id, cancellationToken);
            recording.Fail(RecordingProblem.TextTooLong);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("The text of recording {RecordingId} does not fit in its session", id);
            return false;
        }
    }
}

public static class CompleteTranscription
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.complete")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                return;

            recording.Complete(timeProvider.GetUtcNow());
            if (recording.AudioKeptUntil is { } until)
            {
                await dbContext.SaveAndEnqueueAsync(
                    (jobs, ct) => jobs.EnqueueAsync(new DeleteRecordingAudio(recording.Id, recording.UserId, recording.TenantId), until, ct),
                    cancellationToken);
            }
            else
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.Here().Information("Recording {RecordingId} is {Status}, {Problem}", recording.Id, recording.Status, recording.Problem);
        }
    }
}

public static class RemoveEndedWorkAudio
{
    public sealed record Command(string Id) : IRequest;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.ended")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                RecordingWork.TryDeleteWorkAudio(fileStore, recording.TenantId, recording.UserId, recording.Id, _logger);
        }
    }
}

public static class FailTranscription
{
    public sealed record Command(string Id, RecordingProblem Problem) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.fail")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                return;

            recording.Fail(request.Problem);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Recording {RecordingId} failed with {Problem}", recording.Id, request.Problem);
        }
    }
}

public static class RemoveKeptAudio
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore, TimeProvider timeProvider) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.audio.remove")
                .GetById(request.Id, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (!recording.CanDeleteAudio(now))
                return;

            fileStore.Delete(RecordingFiles.Audio(recording));
            fileStore.Delete(RecordingFiles.WorkAudio(recording));
            recording.MarkAudioDeleted(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Removed the kept audio of recording {RecordingId}", recording.Id);
        }
    }
}
