namespace Recall.Server.Domain.People.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddPersonEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/people", Handle)
        .WithName("AddPerson")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds a Person to the directory of the caller.
    /// </summary>
    public static async Task<CreatedAtRoute<PersonDto>> Handle(
        PersonForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddPerson.Command(dto), cancellationToken);
        return TypedResults.CreatedAtRoute(result, "GetPerson", new { id = result.Id });
    }
}
