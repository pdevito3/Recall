namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ReviewImportEntryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/import-batches/{id}/entries/review", Handle)
        .WithName("ReviewImportEntry")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Sets the whole review of one entry: rejected, the edited words or null, and the edited time or null. The last review
    /// wins. A put of the agent never changes it.
    /// </summary>
    public static async Task<Ok<ImportEntryReviewedDto>> Handle(string id, ImportEntryReviewDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ReviewImportEntry.Command(id, dto), cancellationToken));
}
