namespace Recall.Server.Domain.Corrections.Features;

using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class DeleteCorrectionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/corrections/{id}", Handle)
        .WithName("DeleteCorrection")
        .WithTags("Corrections")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Deletes a Correction of the caller.
    /// </summary>
    public static async Task<NoContent> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteCorrection.Command(id), cancellationToken);
        return TypedResults.NoContent();
    }
}
