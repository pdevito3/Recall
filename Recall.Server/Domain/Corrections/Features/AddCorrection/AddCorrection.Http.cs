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
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds a Correction of the caller.
    /// </summary>
    public static async Task<CreatedAtRoute<CorrectionDto>> Handle(
        CorrectionForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddCorrection.Command(dto), cancellationToken);
        return TypedResults.CreatedAtRoute(result, "GetCorrection", new { id = result.Id });
    }
}
