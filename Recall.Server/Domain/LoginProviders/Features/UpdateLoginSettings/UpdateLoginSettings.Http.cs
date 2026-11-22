namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class UpdateLoginSettingsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/login-settings", Handle)
        .WithName("UpdateLoginSettings")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Turns password login on or off for the install. A turn off is refused until each tenant has an Admin who can sign in with a
    /// provider. Only a person signed in to Recall can change it.
    /// </summary>
    public static async Task<Ok<LoginSettingsDto>> Handle(LoginSettingsForUpdateDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new UpdateLoginSettings.Command(dto), cancellationToken));
}
