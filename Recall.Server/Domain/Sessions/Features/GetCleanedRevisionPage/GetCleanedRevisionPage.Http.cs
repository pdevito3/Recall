namespace Recall.Server.Domain.Sessions.Features;

using System.ComponentModel.DataAnnotations;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetCleanedRevisionPageEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sessions/{id}/cleaned-revisions", Handle)
        .WithName("GetCleanedRevisionPage")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets one page of the Cleaned Revisions of a Session, newest first, with no text. To get the next page, send the
    /// nextCursor of this page as the cursor.
    /// </summary>
    public static async Task<Ok<RevisionPageDto>> Handle(
        string id,
        string? cursor,
        [Range(1, int.MaxValue)] int? pageSize,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = new GetCleanedRevisionPage.Query(id, CursorPageParametersDto.From(cursor, pageSize));
        return TypedResults.Ok(await mediator.Send(query, cancellationToken));
    }
}
