namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddLoginProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/login-providers", Handle)
        .WithName("AddLoginProvider")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Adds an OIDC provider to the login page of the install, with no restart. The client secret goes under the key ring, and no
    /// answer holds it. Only a person signed in to Recall can add one.
    /// </summary>
    public static async Task<Ok<LoginProviderDto>> Handle(LoginProviderForCreationDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new AddLoginProvider.Command(dto), cancellationToken));
}
