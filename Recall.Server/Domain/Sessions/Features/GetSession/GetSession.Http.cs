namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetSessionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sessions/{id}", Handle)
        .WithName("GetSession")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets a Session of the caller, with its Raw and its Cleaned. The Session of another person gives 404.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(
        string id,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetSession.Query(id), cancellationToken));
}
