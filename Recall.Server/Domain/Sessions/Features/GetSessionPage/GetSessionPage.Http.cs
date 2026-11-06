namespace Recall.Server.Domain.Sessions.Features;

using System.ComponentModel.DataAnnotations;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetSessionPageEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sessions", Handle)
        .WithName("GetSessionPage")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets one page of the timeline of the caller, newest first. To get the next page, send the nextCursor of this page
    /// as the cursor. The last page has no nextCursor.
    /// </summary>
    public static async Task<Ok<SessionPageDto>> Handle(
        string? cursor,
        [Range(1, int.MaxValue)] int? pageSize,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = new GetSessionPage.Query(CursorPageParametersDto.From(cursor, pageSize));
        return TypedResults.Ok(await mediator.Send(query, cancellationToken));
    }
}
