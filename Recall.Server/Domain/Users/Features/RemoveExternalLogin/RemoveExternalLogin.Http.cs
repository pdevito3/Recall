namespace Recall.Server.Domain.Users.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Resources.Entrypoints.Http;

public sealed class RemoveExternalLoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/me/external-logins", Handle)
        .WithName("RemoveExternalLogin")
        .WithTags("ExternalLogins")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Removes a link of the caller, named by its issuer and subject. A link that is gone also gives 204. It is refused when the tenant
    /// would keep no Admin who can sign in and manage access. Only a person signed in to Recall can remove one.
    /// </summary>
    public static async Task<NoContent> Handle([FromBody] ExternalLoginForRemovalDto dto, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new RemoveExternalLogin.Command(null, dto.Issuer, dto.Subject), cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed class RemoveUserExternalLoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapDelete("/users/{id}/external-logins", Handle)
        .WithName("RemoveUserExternalLogin")
        .WithTags("ExternalLogins")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Removes a link of a user of the tenant of the caller, named by its issuer and subject. Another user needs manage_user_access and
    /// each permission that the user holds. It is refused when the tenant would keep no Admin who can sign in and manage access.
    /// </summary>
    public static async Task<NoContent> Handle(
        string id, [FromBody] ExternalLoginForRemovalDto dto, IMediator mediator, CancellationToken cancellationToken)
    {
        await mediator.Send(new RemoveExternalLogin.Command(id, dto.Issuer, dto.Subject), cancellationToken);
        return TypedResults.NoContent();
    }
}
