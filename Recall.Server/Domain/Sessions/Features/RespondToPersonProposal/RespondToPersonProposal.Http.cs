namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RespondToPersonProposalEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/people-proposals/respond", Handle)
        .WithName("RespondToPersonProposal")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Approves or drops a People proposal. An approval tags each place in Cleaned, as a new Cleaned Revision, and Raw does not change.
    /// A proposal that the Session no longer holds gets 200 and changes nothing, so a retry is safe.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(string id, PersonProposalResponseDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new RespondToPersonProposal.Command(id, dto), cancellationToken));
}
