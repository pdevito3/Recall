namespace Recall.Server.Domain.Summaries.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GenerateSummaryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/summaries/{period}/{date}/generate", Handle)
        .WithName("GenerateSummary")
        .WithTags("Summaries")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Opens a period. A Summary that is missing, stale, or failed starts a run as a job and answers <c>Generating</c> at once.
    /// A Summary that is ready or that runs stays as it is. A period with no sources gets no Summary.
    /// </summary>
    public static async Task<Ok<SummaryDto>> Handle(string period, string date, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GenerateSummary.Command(PeriodKey.ParsePeriod(period), SummaryRoute.ParseDate(date)), cancellationToken));
}
