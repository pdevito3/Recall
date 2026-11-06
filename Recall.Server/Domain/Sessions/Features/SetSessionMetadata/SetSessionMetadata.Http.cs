namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class SetSessionMetadataEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/sessions/{id}/metadata", Handle)
        .WithName("SetSessionMetadata")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Replaces the activity, the moods, and the Topics that the caller chose for a Session. A save from an older
    /// <c>metadataVersion</c> gets 409 and changes nothing, unless the Session already holds these values. Then the response has the
    /// stored Session, so a retry of a save that reached the server gets 200.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(
        string id,
        SessionMetadataChangeDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new SetSessionMetadata.Command(id, dto), cancellationToken));
}
