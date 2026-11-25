namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Resources.Entrypoints.Http;

public sealed class PutImportEntriesEndpoint : IEndpoint
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    public const string TooLargeMessage = "This call is larger than 16 MB. Send fewer entries in each call.";

    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/import-batches/{id}/entries", Handle)
        .WithName("PutImportEntries")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes), new RequestTooLargeDetail(TooLargeMessage))
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds or changes entries of an open batch by Client session ID. Each entry gets an outcome, in the order of the call. The
    /// same call again changes nothing. A put never changes what the person reviewed, and it removes no entry.
    /// </summary>
    public static async Task<Ok<ImportPutResultDto>> Handle(string id, ImportEntriesPutDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new PutImportEntries.Command(id, dto), cancellationToken));
}
