namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class SaveCleanedDraftEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/sessions/{id}/cleaned", Handle)
        .WithName("SaveCleanedDraft")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Saves the Cleaned Draft. A save wins only when its base is the number of the current Draft. Any other save loses and keeps its words as a Revision.
    /// A base must be the number of a Draft whose words the client holds. The save time decides no save.
    /// </summary>
    public static async Task<Ok<DraftSaveResultDto>> Handle(
        string id,
        DraftSaveDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new SaveCleanedDraft.Command(id, dto), cancellationToken));
}
