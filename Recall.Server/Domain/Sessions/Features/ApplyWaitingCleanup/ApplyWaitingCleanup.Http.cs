namespace Recall.Server.Domain.Sessions.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class ApplyWaitingCleanupEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/sessions/{id}/cleanup/waiting/apply", Handle)
        .WithName("ApplyWaitingCleanup")
        .WithTags("Sessions")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Puts the Cleanup result that waits in History into Cleaned. A result waits when Cleaned moved during its run. Send the
    /// number of the waiting Revision and the number of the Cleaned Draft that you read. A newer Cleaned, or a result that no
    /// longer waits, gets 409, so read the Session again. Cleaned before it stays as a Revision.
    /// </summary>
    public static async Task<Ok<SessionDto>> Handle(string id, WaitingCleanupApplyDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ApplyWaitingCleanup.Command(id, dto), cancellationToken));
}
