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
    /// Changes the settings of the caller. A save from an older version gets 409 and changes nothing, unless the row already holds
    /// these values. Then the response has the stored settings, so a retry of a save that reached the server gets 200.
    /// </summary>
    public static async Task<Ok<UserSettingDto>> Handle(
        UserSettingChangeDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ChangeUserSetting.Command(dto), cancellationToken));
}
