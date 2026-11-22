namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class TestLoginProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/login-providers/test", Handle)
        .WithName("TestLoginProvider")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Runs discovery against the issuer of the form and saves nothing. A provider that fails is a 200 with the failure, because this
    /// server worked. Only a person signed in to Recall can test one.
    /// </summary>
    public static async Task<Ok<LoginProviderTestResultDto>> Handle(LoginProviderForTestDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new TestLoginProvider.Command(dto), cancellationToken));
}
