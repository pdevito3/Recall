namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Entrypoints.Http;
using Resources.Jobs;
using Serilog;
using Transcription;

public static class AddRecording
{
    public sealed record Result(RecordingDto Recording, bool IsNew);

    public sealed record Command(string SessionId, RecordingForCreationDto Dto) : IRequest<Result>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, ModelStore modelStore, JobHostStatus jobHostStatus) : IRequestHandler<Command, Result>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var dto = request.Dto;
            ValidationException.Must(Enum.TryParse<RecordingFormat>(dto.Format, out var format) && Enum.GetName(format) == dto.Format,
                "format", "The audio format must be Webm or Mp4.");
            ValidationException.ThrowWhenNullOrWhitespace(dto.ClientRecordingId, "clientRecordingId", "Please provide a client recording ID.");
            ValidationException.ThrowWhenHasCharacterNotStorable(dto.ClientRecordingId, "clientRecordingId", "The client recording ID has a character that is not allowed.");

            var result = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var session = await dbContext.Sessions
                    .TagWithOperationCallSite("recordings.add.session")
                    .AsNoTracking()
                    .GetById(request.SessionId, ct);

                var existing = await dbContext.Recordings
                    .TagWithOperationCallSite("recordings.add.replay")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ClientRecordingId == dto.ClientRecordingId, ct);
                if (existing is not null)
                    return new Result(existing.ToDto([], modelStore.Status, jobHostStatus.IsStopped), false);

                var recording = Recording.Create(owner.TenantId, owner.UserId, session.Id, dto.ClientRecordingId, format);
                await dbContext.Recordings.AddAsync(recording, ct);
                await dbContext.SaveChangesAsync(ct);
                return new Result(recording.ToDto([], modelStore.Status, jobHostStatus.IsStopped), true);
            }, cancellationToken);

            Activity.Current?.SetTag("app.recording_id", result.Recording.Id);
            Activity.Current?.SetTag("app.recording.is_new", result.IsNew);
            _logger.Here().Information("Added recording {RecordingId}, new {IsNew}", result.Recording.Id, result.IsNew);
            return result;
        }
    }
}

public sealed class AddRecordingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/recordings", Handle)
        .WithName("AddRecording")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Starts a Recording in a Session of the caller, or returns the one that has this client recording ID. The first create
    /// gives 201 and a replay gives 200, so a device can send it again after a lost response.
    /// </summary>
    public static async Task<Results<CreatedAtRoute<RecordingDto>, Ok<RecordingDto>>> Handle(
        string id, RecordingForCreationDto dto, IMediator mediator, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddRecording.Command(id, dto), cancellationToken);
        return result.IsNew
            ? TypedResults.CreatedAtRoute(result.Recording, "GetRecording", new { id = result.Recording.Id })
            : TypedResults.Ok(result.Recording);
    }
}
