namespace Recall.Server.Domain.UserSettings.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ChangeUserSettingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/me/settings", Handle)
        .WithName("ChangeUserSetting")
        .WithTags("UserSettings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Changes the settings of the caller, with Last-write-wins on the save time. A change with a save time that is not newer than
    /// the stored settings does nothing, and the response has the stored settings.
    /// </summary>
    public static async Task<Ok<UserSettingDto>> Handle(
        UserSettingChangeDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ChangeUserSetting.Command(dto), cancellationToken));
}
