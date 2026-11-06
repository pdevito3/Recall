namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Jobs;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Entrypoints.Http;
using Resources.Storage;
using Serilog;
using Transcription;

public static class FinishRecording
{
    public sealed record Command(string Id, RecordingFinishDto Dto) : IRequest<RecordingDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore, ModelStore modelStore, TimeProvider timeProvider)
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
                    await RecordingWork.TryQueueTranscriptionAsync(dbContext, recording, timeProvider, _logger);
                return recording.ToDto(stored, modelStore.Status);
            }

            recording.Finish(stored, request.Dto.SliceCount, request.Dto.ByteLength);
            await fileStore.WriteAsync(RecordingFiles.Audio(recording), async (target, ct) =>
            {
                foreach (var slice in slices)
                {
                    await using var part = fileStore.OpenRead(RecordingFiles.Slice(recording, slice.Index, slice.Sha256));
                    await part.CopyToAsync(target, ct);
                }
            }, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await RecordingWork.TryQueueTranscriptionAsync(dbContext, recording, timeProvider, _logger);
            RecordingWork.TryDeleteSlices(fileStore, recording, _logger);

            Activity.Current?.SetTag("app.recording.byte_length", recording.ByteLength);
            _logger.Here().Information("Finished the upload of recording {RecordingId}", recording.Id);
            return recording.ToDto(stored, modelStore.Status);
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
    // The status is already saved, so a client that goes away here must not stop the work.
    public static async Task TryQueueTranscriptionAsync(AppDbContext dbContext, Recording recording, TimeProvider timeProvider, ILogger logger)
    {
        try
        {
            await dbContext.SaveAndEnqueueAsync(
                (jobs, ct) => jobs.EnqueueAsync(new TranscribeRecording(recording.Id, recording.UserId, recording.TenantId), timeProvider.GetUtcNow(), ct),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Activity.Current?.SetTag("app.recording.queue_fault", exception.GetType().Name);
            logger.Here().Warning("Could not queue the text of recording {RecordingId}, because of {FaultType}. The sweep queues it within the hour", recording.Id, exception.GetType().Name);
        }
    }

    public static void TryDeleteSlices(FileStore fileStore, Recording recording, ILogger logger)
    {
        try
        {
            fileStore.DeleteUnder(RecordingFiles.Slices(recording));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Here().Warning("Could not delete the slices of recording {RecordingId}, because of {FaultType}", recording.Id, exception.GetType().Name);
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
