namespace Recall.Server.Domain.People.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class DescribePersonEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/people/{id}/details", Handle)
        .WithName("DescribePerson")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Sets the full name and the notes of a Person, which tell People of one label apart. The label and each mention stay.
    /// A version that is not the stored version gives 409.
    /// </summary>
    public static async Task<Ok<PersonDto>> Handle(
        string id,
        PersonDetailsDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new DescribePerson.Command(id, dto), cancellationToken));
}
