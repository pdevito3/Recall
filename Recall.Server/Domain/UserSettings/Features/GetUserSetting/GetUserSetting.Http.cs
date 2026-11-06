namespace Recall.Server.Domain.UserSettings.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetUserSettingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/me/settings", Handle)
        .WithName("GetUserSetting")
        .WithTags("UserSettings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the settings of the caller. A caller with no change yet gets the defaults.
    /// </summary>
    public static async Task<Ok<UserSettingDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetUserSetting.Query(), cancellationToken));
}
