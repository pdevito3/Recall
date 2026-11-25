namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ReviewImportDayEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/import-batches/{id}/days/review", Handle)
        .WithName("ReviewImportDay")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Rejects or keeps a day of a batch. A rejected day holds each entry of its date, also an entry that a later put adds.
    /// An entry that the person rejected alone stays rejected when its day is kept again.
    /// </summary>
    public static async Task<Ok<ImportDayReviewedDto>> Handle(
        string id, ImportDayReviewDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ReviewImportDay.Command(id, dto), cancellationToken));
}
