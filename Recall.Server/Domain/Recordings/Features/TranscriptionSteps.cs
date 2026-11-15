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
using Sessions;
using Transcription;

public static class BeginTranscription
{
    public sealed record Start(string AudioPath, string WorkAudioPath, long StartSample, AudioProvider? Provider, TranscriptionChoice? Choice, int? HighestSlice);

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

            AudioProvider? provider = null;
            TranscriptionChoice? choice = null;
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

                choice = new TranscriptionChoice(chosen.Id, chosen.Version);
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
                    fileStore.LocalPathOf(RecordingFiles.WorkAudio(recording)),
                    recording.TranscribedSamples,
                    provider,
                    choice,
                    recording.HighestStoredSlice);
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

/// <summary>The provider that a cloud job began with. Each change of the provider row, a new choice included, gives a new version.</summary>
public sealed record TranscriptionChoice(string ModelProviderId, uint Version);

public static class TranscriptionChoiceHolds
{
    public sealed record Query(string ModelProviderId, uint Version) : IRequest<bool>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, bool>
    {
        public async Task<bool> Handle(Query request, CancellationToken cancellationToken)
        {
            var now = await dbContext.ModelProviders.AsNoTracking()
                .TagWithOperationCallSite("recordings.transcribe.choice_holds")
                .TranscriptionFirst()
                .Select(p => new { p.Id, p.Version })
                .FirstOrDefaultAsync(cancellationToken);
            return now is not null && now.Id == request.ModelProviderId && now.Version == request.Version;
        }
    }
}

public static class SaveTranscriptChunk
{
    /// <summary>
    /// A chunk of an engine that no longer has the Recording is refused, so a hand-over repeats no text. A chunk that does not
    /// start where the saved text ends is refused too, so text joins with no repeat and no gap whatever the cuts of the run.
    /// <paramref name="WordTimes"/> are sample numbers as the engine gave them, null from the cloud engine, and never stored.
    /// </summary>
    public sealed record Command(string Id, TranscriptionEngineKind Engine, long FromSample, long EndSample, long TotalSamples, string Words,
        IReadOnlyList<WordTime>? WordTimes = null) : IRequest<bool>, IRunsAgainWhenRaced;

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
            if (!recording.Joins(request.FromSample, request.EndSample))
            {
                _logger.Here().Warning("A chunk of recording {RecordingId} from sample {FromSample} does not join its text at {TranscribedSamples}, so its run stops",
                    recording.Id, request.FromSample, recording.TranscribedSamples);
                return false;
            }

            var words = request.Words.Trim();
            if (!StorableText.CanStore(words))
            {
                recording.Fail(RecordingProblem.EngineCrashed);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.Here().Warning("The engine gave text that cannot be stored for recording {RecordingId}", recording.Id);
                return false;
            }

            Session? session = null;
            RawRevision? replaced = null;
            if (words.Length > 0)
            {
                session = await dbContext.Sessions
                    .TagWithOperationCallSite("recordings.transcribe.session")
                    .GetById(recording.SessionId, cancellationToken);
                // When a chunk made the Draft, this chunk holds each of its words, so a Recording keeps one Revision of its
                // text and not one for each chunk (SES-48).
                replaced = await dbContext.RawRevisions
                    .TagWithOperationCallSite("recordings.transcribe.replaced_chunk")
                    .FirstOrDefaultAsync(r => r.SessionId == session.Id && r.Number == session.Raw.DraftRevision && r.FromChunk, cancellationToken);
            }

            var added = recording.TakeChunk(request.EndSample, request.TotalSamples, words, request.WordTimes, draftIsFromChunk: session is null || replaced is not null);
            if (added is not null && session is not null)
            {
                if (!session.TryAppendTranscript(added, timeProvider.GetUtcNow()))
                {
                    dbContext.DiscardLostRace();
                    return await FailForLengthAsync(request.Id, cancellationToken);
                }

                if (replaced is not null)
                    dbContext.RawRevisions.Remove(replaced);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Saved a chunk of recording {RecordingId} up to sample {EndSample}", recording.Id, request.EndSample);
            return true;
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
    /// <summary>The highest slice of the audio at the begin of the run. The answer is false when the audio grew since (REC-24), and the run begins again.</summary>
    public sealed record Command(string Id, int? HighestSlice) : IRequest<bool>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, bool>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.complete")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                return true;
            if (recording.AudioGrewSince(request.HighestSlice))
                return false;

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
            return true;
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
                RecordingWork.TryDeleteWorkAudio(fileStore, recording, _logger);
        }
    }
}

public static class FailTranscription
{
    /// <summary>The fault of a run on audio that grew since its begin (REC-24) fails nothing, and the answer is false, so the run begins again.</summary>
    public sealed record Command(string Id, RecordingProblem Problem, int? HighestSlice) : IRequest<bool>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, bool>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.transcribe.fail")
                .GetById(request.Id, cancellationToken);
            if (!recording.WaitsForTranscription)
                return true;
            if (recording.AudioGrewSince(request.HighestSlice))
                return false;

            recording.Fail(request.Problem);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Recording {RecordingId} failed with {Problem}", recording.Id, request.Problem);
            return true;
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
            // The mark is saved before the files go, so a late part (REC-24) that opened the Recording first keeps its audio.
            // After the mark no slice joins, so all the files of the Recording go: the audio, its decoded copy, and a slice
            // file that a lost save left. A job that runs again after the save removes the files that a fault left.
            if (recording.AudioDeletedAt is null)
            {
                var now = timeProvider.GetUtcNow();
                if (!recording.CanDeleteAudio(now))
                    return;

                recording.MarkAudioDeleted(now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            fileStore.DeleteUnder(RecordingFiles.Folder(recording));
            _logger.Here().Information("Removed the kept audio of recording {RecordingId}", recording.Id);
        }
    }
}
