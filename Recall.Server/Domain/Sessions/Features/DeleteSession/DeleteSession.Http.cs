namespace Recall.Server.Domain.Sessions.Features;

using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class DeleteSessionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/sessions/{id}", Handle)
        .WithName("DeleteSession")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Deletes a Session and each of its Revisions. The sync feed gives each device a tombstone.
    /// </summary>
    public static async Task<NoContent> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteSession.Command(id), cancellationToken);
        return TypedResults.NoContent();
    }
}
