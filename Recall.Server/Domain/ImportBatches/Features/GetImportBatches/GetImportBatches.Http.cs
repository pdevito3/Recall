namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetImportBatchesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/import-batches", Handle)
        .WithName("GetImportBatches")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>Gets each import batch of the caller, newest first. An accepted batch stays, with the counts of its accept.</summary>
    public static async Task<Ok<IReadOnlyList<ImportBatchDto>>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetImportBatches.Query(), cancellationToken));
}
