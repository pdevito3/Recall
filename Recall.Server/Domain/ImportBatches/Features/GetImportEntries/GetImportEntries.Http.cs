namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetImportEntriesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/import-batches/{id}/entries", Handle)
        .WithName("GetImportEntries")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets the entries of a batch in order of their date and position, with the review of the person, the start that the
    /// accept gives each one now, and the version of the batch. <c>from</c> and <c>to</c> limit the dates, both included.
    /// </summary>
    public static async Task<Ok<ImportEntryPageDto>> Handle(
        string id, DateOnly? from, DateOnly? to, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetImportEntries.Query(id, from, to), cancellationToken));
}
