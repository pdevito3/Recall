namespace Recall.Server.Domain.Users.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class IssueTemporaryPasswordEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/users/{id}/temporary-password", Handle)
        .WithName("IssueTemporaryPassword")
        .WithTags("Users")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Issues a temporary password to a user and ends each session and personal access token of that user. The first
    /// issue also sets the login name. The answer holds the password, and no later read gives it again. The password
    /// works for 7 days, and only to pick a new password. Only a person signed in to Recall can issue one.
    /// </summary>
    public static async Task<Ok<TemporaryPasswordDto>> Handle(
        string id,
        TemporaryPasswordForIssueDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new IssueTemporaryPassword.Command(id, dto.LoginName), cancellationToken);
        return TypedResults.Ok(result);
    }
}
