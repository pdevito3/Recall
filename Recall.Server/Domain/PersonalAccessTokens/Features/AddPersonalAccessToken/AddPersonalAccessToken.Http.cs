namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddPersonalAccessTokenEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/personal-access-tokens", Handle)
        .WithName("AddPersonalAccessToken")
        .WithTags("PersonalAccessTokens")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Makes a personal access token of the caller with a grant that the caller holds. The answer holds the value of the
    /// token, and no later read gives it again. Only a person signed in to the app can make a token.
    /// </summary>
    public static async Task<Created<PersonalAccessTokenCreatedDto>> Handle(
        PersonalAccessTokenForCreationDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddPersonalAccessToken.Command(dto), cancellationToken);
        return TypedResults.Created((string?)null, result);
    }
}
