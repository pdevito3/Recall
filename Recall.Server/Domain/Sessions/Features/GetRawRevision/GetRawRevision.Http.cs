namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetRawRevisionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sessions/{id}/raw-revisions/{number}", Handle)
        .WithName("GetRawRevision")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets one Raw Revision of a Session, with its text.
    /// </summary>
    public static async Task<Ok<RevisionDto>> Handle(
        string id,
        int number,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetRawRevision.Query(id, number), cancellationToken));
}
