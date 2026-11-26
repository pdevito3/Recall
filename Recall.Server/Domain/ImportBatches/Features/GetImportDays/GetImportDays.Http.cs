namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetImportDaysEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/import-batches/{id}/days", Handle)
        .WithName("GetImportDays")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets each date of a batch in order, with the count of its entries, the count of the entries that the person rejected
    /// one by one, and the reject of the day, and the version of the batch. A rejected day with no entry is in the list too.
    /// </summary>
    public static async Task<Ok<ImportDaysDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetImportDays.Query(id), cancellationToken));
}
