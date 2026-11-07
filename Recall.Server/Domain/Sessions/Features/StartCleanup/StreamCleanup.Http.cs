namespace Recall.Server.Domain.Sessions.Features;

using Cleanup;
using Dtos;
using MediatR;
using Resources.Entrypoints.Http;

public sealed class StreamCleanupEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/cleanup/stream", Handle)
        .WithName("StreamCleanup")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .Produces<CleanupEventDto>(StatusCodes.Status200OK, "text/event-stream")
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Starts a Cleanup, or joins the one that runs, and sends its state as server-sent events. Each event holds the JSON
    /// <c>{ type, data: { message, stopReason } }</c>. Close the connection at any time. The Cleanup goes on, and its result stays on the Session.
    /// </summary>
    public static async Task<IResult> Handle(
        string id,
        IMediator mediator,
        CleanupWatch watch,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        await mediator.Send(new StartCleanup.Command(id), cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.ServerSentEvents(watch.EventsOfAsync(id, cancellationToken));
    }
}
