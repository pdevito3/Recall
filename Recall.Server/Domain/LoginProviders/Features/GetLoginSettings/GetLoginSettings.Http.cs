namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetLoginSettingsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/login-settings", Handle)
        .WithName("GetLoginSettings")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>Reads whether password login is on for the install. Only a person signed in to Recall can read it.</summary>
    public static async Task<Ok<LoginSettingsDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetLoginSettings.Query(), cancellationToken));
}
