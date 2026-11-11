namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using BackWave.Storage;
using Databases;
using Dtos;
using Exceptions;
using Jobs;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using Resources;
using Resources.Entrypoints.Http;
using Resources.Jobs;
using Resources.Storage;
using Serilog;
using Transcription;

public static class FinishRecording
{
    public sealed record Command(string Id, RecordingFinishDto Dto) : IRequest<RecordingDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore, ModelStore modelStore, JobHostStatus jobHostStatus, IJobStore jobStore, TimeProvider timeProvider)
        : IRequestHandler<Command, RecordingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<RecordingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.finish.load")
                .GetById(request.Id, cancellationToken);
            var slices = await dbContext.RecordingSlices
                .TagWithOperationCallSite("recordings.finish.slices")
                .AsNoTracking()
                .Where(s => s.RecordingId == recording.Id)
                .OrderBy(s => s.Index)
                .ToListAsync(cancellationToken);
            var stored = slices.Select(s => s.Index).ToList();

            if (recording.Status != RecordingStatus.Receiving && request.Dto.SliceCount == stored.Count && request.Dto.ByteLength == recording.ByteLength)
            {
                if (recording.WaitsForTranscription)
                    await RecordingWork.TryQueueTranscriptionAsync(dbContext, jobStore, recording, timeProvider, _logger);
                return recording.ToDto(stored, modelStore.Status, jobHostStatus.TranscriptionIsStopped);
            }

            var joinedThrough = recording.QuietEndThroughSlice;
            recording.Finish(stored, request.Dto.SliceCount, request.Dto.ByteLength, await RecordingWork.ChosenEngineAsync(dbContext, cancellationToken));
            await RecordingWork.WriteAudioAsync(dbContext, fileStore, recording, slices, joinedThrough, _logger, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await RecordingWork.TryQueueTranscriptionAsync(dbContext, jobStore, recording, timeProvider, _logger);
            RecordingWork.TryDeleteJoinedSlices(fileStore, recording, slices, _logger);

            Activity.Current?.SetTag("app.recording.byte_length", recording.ByteLength);
            _logger.Here().Information("Finished the upload of recording {RecordingId}", recording.Id);
            return recording.ToDto(stored, modelStore.Status, jobHostStatus.TranscriptionIsStopped);
        }
    }
}

public sealed class FinishRecordingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/recordings/{id}/finish", Handle)
        .WithName("FinishRecording")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Ends the upload and queues the work that makes the text. A gap in the slices gives 409, and the slices stay, so a
    /// client can read <c>storedSlices</c>, send what is missing, and finish again. A second finish gives the same answer.
    /// </summary>
    public static async Task<Ok<RecordingDto>> Handle(
        string id, RecordingFinishDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new FinishRecording.Command(id, dto), cancellationToken));
}

public static class RecordingWork
{
    /// <summary>The engine of a Recording that is queued now: the provider that an Admin chose for audio, or the local engine.</summary>
    public static async Task<TranscriptionEngineKind> ChosenEngineAsync(AppDbContext dbContext, CancellationToken cancellationToken)
        => await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("recordings.engine.chosen")
            .TranscriptionFirst()
            .AnyAsync(cancellationToken)
            ? TranscriptionEngineKind.Cloud
            : TranscriptionEngineKind.Local;

    /// <summary>
    /// The job of the engine of the Recording. A cloud job goes to the queue of the provider calls that <see cref="CleanupQueues.Pick"/>
    /// gives its tenant, so its caller reads that queue once for each tenant in the callback of the save, where no other start enqueues.
    /// </summary>
    public static async Task EnqueueAsync(JobEnqueuer jobs, Recording recording, string? cloudQueue, DateTimeOffset now, CancellationToken cancellationToken)
        => await EnqueueAsync(jobs, recording.Id, recording.UserId, recording.TenantId, recording.Engine, cloudQueue, now, cancellationToken);

    public static async Task EnqueueAsync(
        JobEnqueuer jobs, string id, string userId, string tenantId, TranscriptionEngineKind engine, string? cloudQueue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (engine == TranscriptionEngineKind.Cloud)
            await jobs.EnqueueOnAsync(new TranscribeRecordingInCloud(id, userId, tenantId), now, cloudQueue!, cancellationToken);
        else
            await jobs.EnqueueAsync(new TranscribeRecording(id, userId, tenantId), now, cancellationToken);
    }

    public static async Task<string?> CloudQueueOfAsync(IJobStore jobStore, string tenantId, TranscriptionEngineKind engine, CancellationToken cancellationToken)
        => engine == TranscriptionEngineKind.Cloud
            ? CleanupQueues.Pick(tenantId, await CleanupQueues.UnfinishedAsync(jobStore, cancellationToken))
            : null;

    // The status is already saved, so a client that goes away here must not stop the work.
    public static async Task TryQueueTranscriptionAsync(AppDbContext dbContext, IJobStore jobStore, Recording recording, TimeProvider timeProvider, ILogger logger)
    {
        try
        {
            await dbContext.SaveAndEnqueueAsync(
                async (jobs, ct) =>
                {
                    // A retry or a repeat finish of a Recording whose cloud job still runs must not start a second one beside it.
                    if (recording.Engine == TranscriptionEngineKind.Cloud && (await CleanupQueues.RecordingsInCloudJobsAsync(jobStore, ct)).Contains(recording.Id))
                        return;
                    await EnqueueAsync(jobs, recording, await CloudQueueOfAsync(jobStore, recording.TenantId, recording.Engine, ct), timeProvider.GetUtcNow(), ct);
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Activity.Current?.SetTag("app.recording.queue_fault", exception.GetType().Name);
            logger.Here().Warning("Could not queue the text of recording {RecordingId}, because of {FaultType}. The sweep queues it within the hour", recording.Id, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Joins the slices, in the order of their index, into the audio file of the Recording. After a quiet end (REC-23) the
    /// file holds the slices through <paramref name="joinedThrough"/>, and their files are gone, so the late part (REC-24)
    /// joins after the bytes of those slices. A repeat reads the same bytes, so it writes the same file. The decoded audio
    /// of the shorter file goes, so the next run decodes the whole file.
    /// </summary>
    /// <remarks>
    /// A slice file goes only after a save that joined it. So a file that is gone here was joined by a rival that saved
    /// first, and the run starts again and reads that save. With no such save the file is lost: the other slices join with
    /// a gap, as after a quiet end, and the Recording says so. With no joined audio, or no slice to join, it writes no
    /// audio, and the transcription fails with <see cref="RecordingProblem.AudioMissing"/>.
    /// </remarks>
    public static async Task WriteAudioAsync(AppDbContext dbContext, FileStore fileStore, Recording recording,
        IReadOnlyList<RecordingSlice> slices, int? joinedThrough, ILogger logger, CancellationToken cancellationToken)
    {
        var joinedBytes = slices.Where(s => s.Index <= (joinedThrough ?? -1)).Sum(s => s.ByteLength);
        var late = slices.Where(s => s.Index > (joinedThrough ?? -1)).OrderBy(s => s.Index).ToList();
        var kept = late.Where(s => fileStore.Exists(SliceFile(recording, s))).ToList();
        var hasJoinedAudio = joinedBytes == 0 || fileStore.Exists(RecordingFiles.Audio(recording));
        if (kept.Count < late.Count || !hasJoinedAudio)
        {
            await StartAgainWhenARivalSavedAsync(dbContext, recording, cancellationToken);
            recording.MarkAudioLost();
            logger.Here().Warning("The audio of recording {RecordingId} lost {LostSlices} slice files and {LostJoinedBytes} joined bytes",
                recording.Id, late.Count - kept.Count, hasJoinedAudio ? 0 : joinedBytes);
            if (!hasJoinedAudio || (joinedBytes == 0 && kept.Count == 0))
                return;
        }

        try
        {
            await fileStore.WriteAsync(RecordingFiles.Audio(recording), async (target, ct) =>
            {
                if (joinedBytes > 0)
                    await CopyJoinedAudioAsync(fileStore, recording, joinedBytes, target, ct);
                foreach (var slice in kept)
                {
                    await using var part = fileStore.OpenRead(SliceFile(recording, slice));
                    await part.CopyToAsync(target, ct);
                }
            }, cancellationToken);
        }
        catch (NotFoundException)
        {
            // A rival saved and removed the files during the write. The write left the audio as it was.
            await StartAgainWhenARivalSavedAsync(dbContext, recording, cancellationToken);
            throw;
        }

        if (joinedThrough is not null)
            TryDeleteWorkAudio(fileStore, recording.TenantId, recording.UserId, recording.Id, logger);
    }

    private static FileKey SliceFile(Recording recording, RecordingSlice slice) => RecordingFiles.Slice(recording, slice.Index, slice.Sha256);

    private static async Task StartAgainWhenARivalSavedAsync(AppDbContext dbContext, Recording recording, CancellationToken cancellationToken)
    {
        var saved = await dbContext.Recordings
            .TagWithOperationCallSite("recordings.audio.saved_version")
            .AsNoTracking()
            .Where(r => r.Id == recording.Id)
            .Select(r => (uint?)r.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (saved != recording.Version)
            throw new StaleVersionException();
    }

    private static async Task CopyJoinedAudioAsync(FileStore fileStore, Recording recording, long joinedBytes, Stream target, CancellationToken cancellationToken)
    {
        await using var joined = fileStore.OpenRead(RecordingFiles.Audio(recording));
        var buffer = new byte[81_920];
        var left = joinedBytes;
        while (left > 0)
        {
            var read = await joined.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);
            if (read == 0)
                throw new IOException($"The audio of recording {recording.Id} is shorter than its joined slices.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            left -= read;
        }
    }

    /// <summary>
    /// Call after the save that joined <paramref name="joined"/>. Only their files go: a late slice (REC-24) can write its
    /// file beside them before its own save.
    /// </summary>
    public static void TryDeleteJoinedSlices(FileStore fileStore, Recording recording, IEnumerable<RecordingSlice> joined, ILogger logger)
    {
        try
        {
            foreach (var slice in joined)
                fileStore.Delete(SliceFile(recording, slice));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the slices of recording {RecordingId}, because of {FaultType}", recording.Id, exception.GetType().Name);
        }
    }

    public static void TryDeleteWorkAudio(FileStore fileStore, string tenantId, string userId, string recordingId, ILogger logger)
    {
        try
        {
            fileStore.Delete(RecordingFiles.WorkAudio(tenantId, userId, recordingId));
            fileStore.Delete(RecordingFiles.PartialWorkAudio(tenantId, userId, recordingId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the decoded audio of recording {RecordingId}, because of {FaultType}. The next start tries again", recordingId, exception.GetType().Name);
        }
    }

    public static void TryDeleteFolder(FileStore fileStore, FileKey folder, string ownerOfFolder, ILogger logger)
    {
        try
        {
            fileStore.DeleteUnder(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the audio folder of {FolderOwnerId}, because of {FaultType}. The next start tries again", ownerOfFolder, exception.GetType().Name);
        }
    }
}
