namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RespondToSuggestionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup)
    {
        versionedGroup.MapPost("/sessions/{id}/suggestions/accept", Accept)
            .WithName("AcceptSuggestion")
            .WithTags("Sessions")
            .MapToApiVersion(ApiVersions.V1)
            .RequireAuthorization()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesValidationProblem(StatusCodes.Status409Conflict);

        versionedGroup.MapPost("/sessions/{id}/suggestions/reject", Reject)
            .WithName("RejectSuggestion")
            .WithTags("Sessions")
            .MapToApiVersion(ApiVersions.V1)
            .RequireAuthorization()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesValidationProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Adds a suggested Topic as an AI Topic, or a suggested Mood as a Mood, and moves <c>metadataVersion</c>. A Topic that the
    /// User holds stays the Topic of the User. A suggestion that the Session no longer offers gets 200 and changes nothing, so a retry is safe.
    /// </summary>
    public static async Task<Ok<SessionDto>> Accept(string id, SuggestionResponseDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RespondToSuggestion.Command(id, dto, Accept: true), cancellationToken));

    /// <summary>Removes a suggestion. A suggestion that the Session no longer offers gets 200 and changes nothing, so a retry is safe.</summary>
    public static async Task<Ok<SessionDto>> Reject(string id, SuggestionResponseDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RespondToSuggestion.Command(id, dto, Accept: false), cancellationToken));
}
