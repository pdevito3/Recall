namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources;
using Resources.Entrypoints.Http;
using Resources.Jobs;
using Resources.Storage;
using Serilog;
using Transcription;

public static class RetryRecording
{
    public sealed record Command(string Id) : IRequest<RecordingDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, ModelStore modelStore, JobHostStatus jobHostStatus, TimeProvider timeProvider) : IRequestHandler<Command, RecordingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<RecordingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.retry.load")
                .GetById(request.Id, cancellationToken);
            recording.Retry();
            await dbContext.SaveChangesAsync(cancellationToken);
            await RecordingWork.TryQueueTranscriptionAsync(dbContext, recording, timeProvider, _logger);

            _logger.Here().Information("Queued recording {RecordingId} again", recording.Id);
            return recording.ToDto([], modelStore.Status, jobHostStatus.TranscriptionIsStopped);
        }
    }
}

public sealed class RetryRecordingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/recordings/{id}/retry", Handle)
        .WithName("RetryRecording")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>Queues a Recording that failed or that still waits for its text. It goes on after the text that is saved, so no text repeats.</summary>
    public static async Task<Ok<RecordingDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RetryRecording.Command(id), cancellationToken));
}

public static class DeleteRecording
{
    public sealed record Command(string Id) : IRequest, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.delete.load")
                .GetById(request.Id, cancellationToken);
            dbContext.Recordings.Remove(recording);
            await dbContext.SaveChangesAsync(cancellationToken);
            RecordingWork.TryDeleteFolder(fileStore, RecordingFiles.Folder(recording), recording.Id, _logger);

            _logger.Here().Information("Deleted recording {RecordingId}", recording.Id);
        }
    }
}

public sealed class DeleteRecordingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/recordings/{id}", Handle)
        .WithName("DeleteRecording")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>Deletes a Recording of the caller and its audio. Text that is saved in the Session stays.</summary>
    public static async Task<NoContent> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteRecording.Command(id), cancellationToken);
        return TypedResults.NoContent();
    }
}
