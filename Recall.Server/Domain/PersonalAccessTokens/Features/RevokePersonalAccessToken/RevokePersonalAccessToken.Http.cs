namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class RevokePersonalAccessTokenEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/personal-access-tokens/{id}/revoke", Handle)
        .WithName("RevokePersonalAccessToken")
        .WithTags("PersonalAccessTokens")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Revokes a personal access token. Its next request gets 401. A token that has ended stays as it is, so a second
    /// revoke also gives 204. Its person, or a caller with manage_user_access in the tenant, can revoke it.
    /// </summary>
    public static async Task<NoContent> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new RevokePersonalAccessToken.Command(id), cancellationToken);
        return TypedResults.NoContent();
    }
}
