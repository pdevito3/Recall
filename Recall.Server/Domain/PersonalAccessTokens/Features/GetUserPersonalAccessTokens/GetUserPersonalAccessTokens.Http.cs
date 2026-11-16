namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetUserPersonalAccessTokensEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/users/{id}/personal-access-tokens", Handle)
        .WithName("GetUserPersonalAccessTokens")
        .WithTags("PersonalAccessTokens")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Gets the personal access tokens of a user of the tenant of the caller. Another user needs read_users.</summary>
    public static async Task<Ok<IReadOnlyList<PersonalAccessTokenDto>>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetUserPersonalAccessTokens.Query(id), cancellationToken));
}
