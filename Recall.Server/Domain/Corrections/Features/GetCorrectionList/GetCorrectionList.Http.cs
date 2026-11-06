namespace Recall.Server.Domain.Corrections.Features;

using System.ComponentModel.DataAnnotations;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources;
using Resources.Entrypoints.Http;

public sealed class GetCorrectionListEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/corrections", Handle)
        .WithName("GetCorrectionList")
        .WithTags("Corrections")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets a paginated list of the Corrections of the caller, by term.
    /// </summary>
    public static async Task<Ok<PagedList<CorrectionDto>>> Handle(
        [Range(1, BasePaginationParameters.MaxPageNumber)] int? pageNumber,
        [Range(1, int.MaxValue)] int? pageSize,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetCorrectionList.Query(CorrectionParametersDto.From(pageNumber, pageSize)), cancellationToken));
}
