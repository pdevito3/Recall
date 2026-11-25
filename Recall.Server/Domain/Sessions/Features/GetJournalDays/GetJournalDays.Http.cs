namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetJournalDaysEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/journal-days", Handle)
        .WithName("GetJournalDays")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Gets each Journaling day from <c>from</c> to <c>to</c>, both included, that has a Session, with the count of its
    /// Sessions. An import agent uses it to find the days that the journal already has. The range is at most 366 days.
    /// </summary>
    public static async Task<Ok<IReadOnlyList<JournalDayDto>>> Handle(DateOnly from, DateOnly to, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetJournalDays.Query(from, to), cancellationToken));
}
