namespace Recall.Server.Domain.Corrections.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddCorrectionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/corrections", Handle)
        .WithName("AddCorrection")
        .WithTags("Corrections")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds a Correction of the caller, or returns the Correction that already has the ID that the body names. The
    /// first create gives 201 and a replay gives 200, so a device can send it again after a lost response. A replay of
    /// a deleted Correction gives 409.
    /// </summary>
    public static async Task<Results<CreatedAtRoute<CorrectionDto>, Ok<CorrectionDto>>> Handle(
        CorrectionForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddCorrection.Command(dto), cancellationToken);
        return result.IsNew
            ? TypedResults.CreatedAtRoute(result.Correction, "GetCorrection", new { id = result.Correction.Id })
            : TypedResults.Ok(result.Correction);
    }
}
