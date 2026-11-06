namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Jobs;
using MediatR;
using Resources;
using Resources.Storage;
using Serilog;

public static class BeginTranscription
{
    public sealed record Start(string AudioPath, string WorkFolder, long StartSample);

    public sealed record Command(string Id, bool EngineIsReady) : IRequest<Start?>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Command, Start?>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Start?> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.begin")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                return null;

            RecordingWork.TryDeleteSlices(fileStore, recording, _logger);
            Start? start = null;
            if (!request.EngineIsReady)
            {
                recording.WaitForModel();
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
                    recording.TranscribedSamples);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Recording {RecordingId} is {Status}, {Problem}", recording.Id, recording.Status, recording.Problem);
            return start;
        }
    }
}

public static class SaveTranscriptChunk
{
    public sealed record Command(string Id, long EndSample, long TotalSamples, string Words) : IRequest<bool>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, bool>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.chunk")
                .GetById(request.Id, cancellationToken);

            var words = request.Words.Trim();
            var hasWords = words.Length > 0 && StorableText.CanStore(words);
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

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore, TimeProvider timeProvider) : IRequestHandler<Command>
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

            fileStore.Delete(RecordingFiles.WorkAudio(recording));
            _logger.Here().Information("Recording {RecordingId} is {Status}, {Problem}", recording.Id, recording.Status, recording.Problem);
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
            recording.MarkAudioDeleted(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Removed the kept audio of recording {RecordingId}", recording.Id);
        }
    }
}
