namespace Recall.Server.Domain.Users.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetExternalLoginsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/me/external-logins", Handle)
        .WithName("GetExternalLogins")
        .WithTags("ExternalLogins")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the links of the caller to identities at providers, oldest first. Only a person signed in to Recall can read them.
    /// </summary>
    public static async Task<Ok<IReadOnlyList<ExternalLoginDto>>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetExternalLogins.Query(null), cancellationToken));
}

public sealed class GetUserExternalLoginsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/users/{id}/external-logins", Handle)
        .WithName("GetUserExternalLogins")
        .WithTags("ExternalLogins")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Gets the links of a user of the tenant of the caller. Another user needs manage_user_access and each permission that the user
    /// holds. Only a person signed in to Recall can read them.
    /// </summary>
    public static async Task<Ok<IReadOnlyList<ExternalLoginDto>>> Handle(string id, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetExternalLogins.Query(id), cancellationToken));
}
