namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RecordCleanupResultEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/cleanup/result", Handle)
        .WithName("RecordCleanupResult")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Stores the Cleanup of a model on the device as a new Cleaned Revision, and Raw does not change. A call while a Cleanup job
    /// runs gets 422. A result applies once. A repeat of it gets 200 with the stored Session and writes nothing, so a later hand
    /// edit, tag, or Cleanup stays.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(string id, CleanupResultDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RecordCleanupResult.Command(id, dto), cancellationToken));
}
