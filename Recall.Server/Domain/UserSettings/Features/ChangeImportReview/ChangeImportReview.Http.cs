namespace Recall.Server.Domain.UserSettings.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ChangeImportReviewEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/me/settings/import-review", Handle)
        .WithName("ChangeImportReview")
        .WithTags("UserSettings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Turns the import review on or off. Only a web session of the person changes it, so no agent can turn off the review
    /// that guards its own accept. A save from another version gets 409, unless the row already holds this value.
    /// </summary>
    public static async Task<Ok<UserSettingDto>> Handle(ImportReviewChangeDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ChangeImportReview.Command(dto), cancellationToken));
}
