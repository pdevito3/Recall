namespace Recall.Server.Domain.Summaries.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetSummaryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/summaries/{period}/{date}", Handle)
        .WithName("GetSummary")
        .WithTags("Summaries")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Reads the Summary of the period of the caller that holds the date, as yyyy-MM-dd. It starts no run. A period with no Summary gives <c>Missing</c>.
    /// </summary>
    public static async Task<Ok<SummaryDto>> Handle(string period, string date, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetSummary.Query(PeriodKey.ParsePeriod(period), SummaryRoute.ParseDate(date)), cancellationToken));
}
