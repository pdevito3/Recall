namespace Recall.Server.Domain.ModelProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddModelProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/model-providers", Handle)
        .WithName("AddModelProvider")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Connects a model provider and makes it the active one. The key goes under the key ring, and no answer holds it.
    /// </summary>
    public static async Task<Ok<ModelProviderDto>> Handle(
        ModelProviderForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new AddModelProvider.Command(dto), cancellationToken));
}
