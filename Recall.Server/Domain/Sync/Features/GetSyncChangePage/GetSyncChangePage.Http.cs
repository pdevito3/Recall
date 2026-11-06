namespace Recall.Server.Domain.Sync.Features;

using System.ComponentModel.DataAnnotations;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;
using Sessions.Dtos;

public sealed class GetSyncChangePageEndpoint : IEndpoint
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/sync/changes", Handle)
        .WithName("GetSyncChangePage")
        .WithTags("Sync")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Gets the journal rows of the caller that changed after the cursor, and a tombstone for each row that is gone. Send
    /// no cursor for the first pull. A 422 means that the cursor came from before a restore of a backup: keep the saves
    /// that did not reach the server, drop the rows that came from the feed, and pull from the start.
    /// </summary>
    public static async Task<Ok<SyncChangePageDto>> Handle(
        string? cursor,
        [Range(1, int.MaxValue)] int? pageSize,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = new GetSyncChangePage.Query(CursorPageParametersDto.From(cursor, pageSize, DefaultPageSize, MaxPageSize));
        return TypedResults.Ok(await mediator.Send(query, cancellationToken));
    }
}
