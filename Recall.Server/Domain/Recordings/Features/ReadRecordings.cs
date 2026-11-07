namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Resources.Entrypoints.Http;
using Resources.Jobs;
using Resources.Storage;
using Transcription;

public static class GetRecording
{
    public sealed record Query(string Id) : IRequest<RecordingDto>;

    public sealed class Handler(AppDbContext dbContext, ModelStore modelStore, JobHostStatus jobHostStatus) : IRequestHandler<Query, RecordingDto>
    {
        public async Task<RecordingDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.get")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            var stored = await dbContext.RecordingSlices
                .TagWithOperationCallSite("recordings.get.slices")
                .AsNoTracking()
                .Where(s => s.RecordingId == recording.Id)
                .OrderBy(s => s.Index)
                .Select(s => s.Index)
                .ToListAsync(cancellationToken);
            return recording.ToDto(stored, modelStore.Status, jobHostStatus.IsStopped);
        }
    }
}

public sealed class GetRecordingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/recordings/{id}", Handle)
        .WithName("GetRecording")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Gets a Recording of the caller, with its status and, when it has no text, the reason.</summary>
    public static async Task<Ok<RecordingDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetRecording.Query(id), cancellationToken));
}

public static class GetSessionRecordings
{
    public sealed record Query(string SessionId) : IRequest<IReadOnlyList<RecordingDto>>;

    public sealed class Handler(AppDbContext dbContext, ModelStore modelStore, JobHostStatus jobHostStatus) : IRequestHandler<Query, IReadOnlyList<RecordingDto>>
    {
        public async Task<IReadOnlyList<RecordingDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.SessionId);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("recordings.list.session")
                .AsNoTracking()
                .GetById(request.SessionId, cancellationToken);
            var recordings = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.list")
                .AsNoTracking()
                .Where(r => r.SessionId == session.Id)
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken);
            return recordings.Select(r => r.ToDto([], modelStore.Status, jobHostStatus.IsStopped)).ToList();
        }
    }
}

public sealed class GetSessionRecordingsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sessions/{id}/recordings", Handle)
        .WithName("GetSessionRecordings")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Lists the Recordings of a Session of the caller. Read one Recording to get its stored slices.</summary>
    public static async Task<Ok<IReadOnlyList<RecordingDto>>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetSessionRecordings.Query(id), cancellationToken));
}

public static class GetTranscriptionStatus
{
    public const string JobsStoppedText =
        "The background work that turns audio into text stopped. Your audio and your words are safe, and no new text comes until the Owner restarts the server.";

    public sealed record Query : IRequest<TranscriptionStatusDto>;

    public sealed class Handler(AppDbContext dbContext, JobHostStatus jobHostStatus, ModelStore modelStore)
        : IRequestHandler<Query, TranscriptionStatusDto>
    {
        public Task<TranscriptionStatusDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            return Task.FromResult(StatusOf(jobHostStatus.StoppedCause(), modelStore.Status));
        }
    }

    public static TranscriptionStatusDto StatusOf(string? jobsStoppedCause, ModelStatus model) => new()
    {
        JobsStopped = jobsStoppedCause is not null,
        JobsStoppedText = jobsStoppedCause is null ? null : JobsStoppedText,
        Model = model.ToString(),
        ModelText = RecordingViews.ModelTextOf(model),
    };
}

public sealed class GetTranscriptionStatusEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/recordings/status", Handle)
        .WithName("GetTranscriptionStatus")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>Says if the work behind all audio runs, and what to do when it does not. The capture page shows this.</summary>
    public static async Task<Ok<TranscriptionStatusDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetTranscriptionStatus.Query(), cancellationToken));
}

public static class GetRecordingFile
{
    public sealed record Result(Stream Content, string ContentType, string FileName);

    public sealed record Query(string Id, string Name) : IRequest<Result>;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Query, Result>
    {
        public async Task<Result> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.file.load")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);

            if (request.Name != recording.AudioFileName || recording.Status == RecordingStatus.Receiving || recording.AudioDeletedAt is not null)
                throw new Exceptions.NotFoundException("The file was not found.");

            return new Result(fileStore.OpenRead(RecordingFiles.Audio(recording)), recording.AudioContentType, recording.AudioFileName);
        }
    }
}

public sealed class GetRecordingFileEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/recordings/{id}/files/{name}", Handle)
        .WithName("GetRecordingFile")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .Produces<Stream>(StatusCodes.Status200OK, "audio/webm", "audio/mp4")
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Downloads the audio of a Recording of the caller, as an attachment. A range request is allowed.</summary>
    public static async Task<IResult> Handle(string id, string name, HttpResponse response, IMediator mediator, CancellationToken cancellationToken)
    {
        var file = await mediator.Send(new GetRecordingFile.Query(id, name), cancellationToken);
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: true);
    }
}
