namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddSessionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions", Handle)
        .WithName("AddSession")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds a Session, or returns the Session that already has this client session ID. The first create gives 201 and a
    /// replay gives 200, so a device can send it again after a lost response. A replay of a deleted Session gives 409.
    /// </summary>
    public static async Task<Results<CreatedAtRoute<SessionDto>, Ok<SessionDto>>> Handle(
        SessionForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddSession.Command(dto), cancellationToken);
        return result.IsNew
            ? TypedResults.CreatedAtRoute(result.Session, "GetSession", new { id = result.Session.Id })
            : TypedResults.Ok(result.Session);
    }
}
