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
            recording.Finish(stored, request.Dto.SliceCount, request.Dto.ByteLength, await RecordingWork.ChosenEngineAsync(dbContext, cancellationToken),
                request.Dto.ParagraphTapsMs);
            await RecordingWork.JoinAudioAndSaveAsync(dbContext, fileStore, recording, slices, joinedThrough, _logger, cancellationToken);
            await RecordingWork.TryQueueTranscriptionAsync(dbContext, jobStore, recording, timeProvider, _logger);

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
    /// The one commit point of the audio, for the finish and the quiet end (REC-23). The audio that the row names, then the
    /// slices above <paramref name="joinedThrough"/> in the order of their index, join into a new file of a new name, and
    /// the version-checked save that names it makes it the audio. So a step that loses its save changes no file that the
    /// row names: it deletes only its own new file. After the save the files that the row no longer names go, the old
    /// audio, its decoded copy, and the joined slices, and the next run decodes the new file.
    /// </summary>
    /// <remarks>
    /// The step reads each file where it checks it, so a file is either in the new audio or counted as lost. A file can be
    /// gone because a rival saved first and deleted it. Then the save of the step loses on the version, and the step
    /// starts again and reads the save of the rival. With no such save the file is lost: the other slices join with a gap,
    /// and the Recording says so. Lost joined audio leaves no audio to add the late part to, as the sample pointer counts
    /// its samples, so the step writes no audio, and the transcription fails with <see cref="RecordingProblem.AudioMissing"/>.
    /// </remarks>
    public static async Task JoinAudioAndSaveAsync(AppDbContext dbContext, FileStore fileStore, Recording recording,
        IReadOnlyList<RecordingSlice> slices, int? joinedThrough, ILogger logger, CancellationToken cancellationToken)
    {
        var joined = recording.AudioFile;
        var late = slices.Where(s => s.Index > (joinedThrough ?? -1)).OrderBy(s => s.Index).ToList();
        var written = await WriteJoinedAudioAsync(fileStore, recording, joined, late, logger, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (written is not null)
                TryDelete(fileStore, recording, logger, written);
            throw;
        }

        if (written is not null && joined is not null)
        {
            TryDelete(fileStore, recording, logger,
                RecordingFiles.Audio(recording, joined), RecordingFiles.WorkAudio(recording, joined), RecordingFiles.PartialWorkAudio(recording, joined));
        }

        TryDelete(fileStore, recording, logger, [.. slices.Select(s => SliceFile(recording, s))]);
    }

    /// <summary>The new audio file, or null when the step has no audio to write. It marks the Recording when a file is lost.</summary>
    private static async Task<FileKey?> WriteJoinedAudioAsync(FileStore fileStore, Recording recording, string? joined,
        List<RecordingSlice> late, ILogger logger, CancellationToken cancellationToken)
    {
        await using var joinedAudio = joined is null ? null : TryOpenRead(fileStore, RecordingFiles.Audio(recording, joined));
        if (joined is not null && joinedAudio is null)
        {
            recording.MarkAudioLost();
            logger.Here().Warning("The joined audio of recording {RecordingId} is lost, so its {LateSlices} late slices join no audio", recording.Id, late.Count);
            return null;
        }

        var name = recording.NewAudioFileName();
        var target = RecordingFiles.Audio(recording, name);
        var joinedParts = joinedAudio is null ? 0 : 1;
        var lostSlices = 0;
        await fileStore.WriteAsync(target, async (stream, ct) =>
        {
            if (joinedAudio is not null)
                await joinedAudio.CopyToAsync(stream, ct);
            foreach (var slice in late)
            {
                await using var part = TryOpenRead(fileStore, SliceFile(recording, slice));
                if (part is null)
                {
                    lostSlices++;
                    continue;
                }

                await part.CopyToAsync(stream, ct);
                joinedParts++;
            }
        }, cancellationToken);

        if (lostSlices > 0)
        {
            recording.MarkAudioLost();
            logger.Here().Warning("The audio of recording {RecordingId} lost {LostSlices} slice files", recording.Id, lostSlices);
        }

        if (joinedParts == 0)
        {
            fileStore.Delete(target);
            return null;
        }

        recording.UseAudioFile(name);
        return target;
    }

    private static Stream? TryOpenRead(FileStore fileStore, FileKey file)
    {
        try
        {
            return fileStore.OpenRead(file);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static FileKey SliceFile(Recording recording, RecordingSlice slice) => RecordingFiles.Slice(recording, slice.Index, slice.Sha256);

    private static void TryDelete(FileStore fileStore, Recording recording, ILogger logger, params FileKey[] files)
    {
        try
        {
            foreach (var file in files)
                fileStore.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the old files of recording {RecordingId}, because of {FaultType}. The removal of its audio deletes them", recording.Id, exception.GetType().Name);
        }
    }

    /// <summary>The decoded copy of the audio that the row names. A copy of an older audio file went with that file.</summary>
    public static void TryDeleteWorkAudio(FileStore fileStore, Recording recording, ILogger logger)
    {
        try
        {
            fileStore.Delete(RecordingFiles.WorkAudio(recording));
            fileStore.Delete(RecordingFiles.PartialWorkAudio(recording));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the decoded audio of recording {RecordingId}, because of {FaultType}. The next start tries again", recording.Id, exception.GetType().Name);
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
