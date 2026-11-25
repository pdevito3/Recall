namespace Recall.Server.Domain.ImportBatches.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RemoveImportEntriesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/import-batches/{id}/entries/remove", Handle)
        .WithName("RemoveImportEntries")
        .WithTags("ImportBatches")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Removes the named entries of an open batch. An entry that the person rejected stays, with its reject, so that a
    /// later put cannot bring it back as kept.
    /// </summary>
    public static async Task<Ok<ImportRemoveResultDto>> Handle(string id, ImportEntriesRemoveDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RemoveImportEntries.Command(id, dto), cancellationToken));
}
