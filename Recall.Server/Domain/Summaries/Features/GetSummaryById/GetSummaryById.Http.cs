namespace Recall.Server.Domain.Summaries.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetSummaryByIdEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/summaries/{id}", Handle)
        .WithName("GetSummaryById")
        .WithTags("Summaries")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Reads a Summary of the caller by its ID. The Summary of another person gives 404.
    /// </summary>
    public static async Task<Ok<SummaryDto>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetSummaryById.Query(id), cancellationToken));
}
