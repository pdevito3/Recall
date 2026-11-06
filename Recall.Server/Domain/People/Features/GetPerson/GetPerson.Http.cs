namespace Recall.Server.Domain.People.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetPersonEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/people/{id}", Handle)
        .WithName("GetPerson")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets a Person of the caller.
    /// </summary>
    public static async Task<Ok<PersonDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetPerson.Query(id), cancellationToken));
}
