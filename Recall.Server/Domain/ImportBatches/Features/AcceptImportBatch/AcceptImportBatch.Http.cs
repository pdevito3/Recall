namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AcceptImportBatchEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/import-batches/{id}/accept", Handle)
        .WithName("AcceptImportBatch")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Makes a Session of each kept entry, in one save, and removes the entries and days of the batch. An accept from another
    /// version gets 409. An accepted batch gives its stored counts again. With the import review on, only a web session of
    /// the person accepts.
    /// </summary>
    public static async Task<Ok<ImportBatchDto>> Handle(string id, ImportBatchAcceptDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new AcceptImportBatch.Command(id, dto), cancellationToken));
}
