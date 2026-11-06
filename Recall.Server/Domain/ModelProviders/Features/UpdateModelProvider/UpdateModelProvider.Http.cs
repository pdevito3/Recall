namespace Recall.Server.Domain.ModelProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class UpdateModelProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/model-providers/{id}", Handle)
        .WithName("UpdateModelProvider")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Changes a model provider. A blank key keeps the stored key.
    /// </summary>
    public static async Task<Ok<ModelProviderDto>> Handle(
        string id,
        ModelProviderForUpdateDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new UpdateModelProvider.Command(id, dto), cancellationToken));
}
