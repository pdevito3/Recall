namespace Recall.Server.Domain.Users.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class AddUserRoleEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPost("/users/{id}/roles", Handle)
        .WithName("AddUserRole")
        .WithTags("Users")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem()
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesValidationProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Adds a role to a User. The permissions of the roles add up. The user then holds each permission of the role, so a removed permission of the role comes back.
    /// </summary>
    public static async Task<Ok<UserDto>> Handle(
        string id,
        UserRoleDto dto,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AddUserRole.Command(id, dto.Role), cancellationToken);
        return TypedResults.Ok(result);
    }
}
