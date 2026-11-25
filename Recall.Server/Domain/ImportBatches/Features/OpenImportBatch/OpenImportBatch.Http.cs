namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class OpenImportBatchEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/import-batches", Handle)
        .WithName("OpenImportBatch")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Opens an import batch: a draft area outside the journal, where an agent puts the entries of another app. No entry is
    /// in the journal until the person accepts the batch.
    /// </summary>
    public static async Task<Created<ImportBatchDto>> Handle(ImportBatchForCreationDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Created((string?)null, await mediator.Send(new OpenImportBatch.Command(dto), cancellationToken));
}
