namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetPersonalAccessTokensEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/personal-access-tokens", Handle)
        .WithName("GetPersonalAccessTokens")
        .WithTags("PersonalAccessTokens")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the personal access tokens of the caller, newest first. A revoked or expired token stays in the list until
    /// the sweep removes it.
    /// </summary>
    public static async Task<Ok<IReadOnlyList<PersonalAccessTokenDto>>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetPersonalAccessTokens.Query(), cancellationToken));
}
