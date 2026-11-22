namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class UpdateLoginProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/login-providers/{id}", Handle)
        .WithName("UpdateLoginProvider")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Changes a login provider of the app. A blank secret keeps the stored one, and the issuer cannot change. Only a person signed
    /// in to Recall can change one.
    /// </summary>
    public static async Task<Ok<LoginProviderDto>> Handle(string id, LoginProviderForUpdateDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new UpdateLoginProvider.Command(id, dto), cancellationToken));
}
