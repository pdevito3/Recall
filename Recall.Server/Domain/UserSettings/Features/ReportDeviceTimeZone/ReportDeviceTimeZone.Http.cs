namespace Recall.Server.Domain.UserSettings.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ReportDeviceTimeZoneEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/me/settings/device-time-zone", Handle)
        .WithName("ReportDeviceTimeZone")
        .WithTags("UserSettings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Takes the zone that a device is in now. A row that follows the device stores it, and the Journaling day of new Sessions comes
    /// from it. A Fixed row stays as it is. The call sends no version, so a client can send it at each start with no read first.
    /// </summary>
    public static async Task<Ok<UserSettingDto>> Handle(
        DeviceTimeZoneDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ReportDeviceTimeZone.Command(dto), cancellationToken));
}
