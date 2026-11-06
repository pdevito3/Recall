namespace Recall.Server.Domain.ModelProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class TestModelProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/model-providers/{id}/test", Handle)
        .WithName("TestModelProvider")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Asks the provider for its list of models. A refusal is a 200 with the words of the provider, because this server worked.
    /// </summary>
    public static async Task<Ok<ModelProviderTestResultDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new TestModelProvider.Command(id), cancellationToken));
}
