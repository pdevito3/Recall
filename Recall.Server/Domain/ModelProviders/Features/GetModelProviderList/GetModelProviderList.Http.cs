namespace Recall.Server.Domain.ModelProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetModelProviderListEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/model-providers", Handle)
        .WithName("GetModelProviders")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the model providers of the tenant and the state of the key ring. The first one is the active one.
    /// </summary>
    public static async Task<Ok<ModelProviderListDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetModelProviderList.Query(), cancellationToken));
}
