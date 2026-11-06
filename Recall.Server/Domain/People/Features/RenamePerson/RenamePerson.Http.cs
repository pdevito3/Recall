namespace Recall.Server.Domain.People.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RenamePersonEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/people/{id}", Handle)
        .WithName("RenamePerson")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Renames a Person. Each mention in each Session changes its label, and each text that changes makes a Revision.
    /// </summary>
    public static async Task<Ok<PersonDto>> Handle(
        string id,
        PersonLabelDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RenamePerson.Command(id, dto), cancellationToken));
}
