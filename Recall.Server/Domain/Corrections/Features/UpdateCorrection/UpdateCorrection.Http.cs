namespace Recall.Server.Domain.Corrections.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class UpdateCorrectionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/corrections/{id}", Handle)
        .WithName("UpdateCorrection")
        .WithTags("Corrections")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Updates a Correction of the caller.
    /// </summary>
    public static async Task<Ok<CorrectionDto>> Handle(
        string id,
        CorrectionForUpdateDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new UpdateCorrection.Command(id, dto), cancellationToken));
}
