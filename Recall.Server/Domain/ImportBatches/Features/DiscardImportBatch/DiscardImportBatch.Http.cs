namespace Recall.Server.Domain.ImportBatches.Features;

using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class DiscardImportBatchEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/import-batches/{id}", Handle)
        .WithName("DiscardImportBatch")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Deletes an open batch with each of its entries and days. It leaves no Tombstone, so each Client session ID of the
    /// batch is free for a later import. An accepted batch gets 409, because its Sessions are in the journal.
    /// </summary>
    public static async Task<NoContent> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new DiscardImportBatch.Command(id), cancellationToken);
        return TypedResults.NoContent();
    }
}
