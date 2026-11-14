namespace Recall.Server.Domain.People.Features;

using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class DeletePersonEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/people/{id}", Handle)
        .WithName("DeletePerson")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Deletes a Person of the caller. Each mention of the Person in the Raw and the Cleaned of each Session becomes its label as plain words, and each text that changes makes a Revision. Older Revisions keep their mentions, so a client shows a mention of a Person that the directory does not have as its label. Send `ifVersion`, the version that the caller saw, so that a Person that a rename changed after it stays and the reply is 409. With no `ifVersion`, the delete goes at any version.
    /// </summary>
    public static async Task<NoContent> Handle(string id, uint? ifVersion, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeletePerson.Command(id, ifVersion), cancellationToken);
        return TypedResults.NoContent();
    }
}
