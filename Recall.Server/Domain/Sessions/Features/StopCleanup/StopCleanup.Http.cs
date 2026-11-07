namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class StopCleanupEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/cleanup/stop", Handle)
        .WithName("StopCleanup")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Stops a running Cleanup. Raw and Cleaned stay as they are, and a new Cleanup can start. A Session with no running Cleanup stays as it is.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new StopCleanup.Command(id), cancellationToken));
}
