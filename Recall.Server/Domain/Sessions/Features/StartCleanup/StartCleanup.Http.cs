namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class StartCleanupEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/cleanup", Handle)
        .WithName("StartCleanup")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Starts a Cleanup as a job and answers at once. While a Cleanup runs, a second call joins it. The result stays when the client goes away.
    /// </summary>
    public static async Task<Accepted<SessionDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Accepted((string?)null, await mediator.Send(new StartCleanup.Command(id), cancellationToken));
}
