namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class SaveRawDraftEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/sessions/{id}/draft", Handle)
        .WithName("SaveRawDraft")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Saves the Raw Draft with Last-write-wins on the save time. A save that loses keeps its words as a Revision.
    /// A base must be the number of a Draft whose words the client holds. A higher base loses at any save time.
    /// </summary>
    public static async Task<Ok<DraftSaveResultDto>> Handle(
        string id,
        DraftSaveDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new SaveRawDraft.Command(id, dto), cancellationToken));
}
