namespace Recall.Server.Domain.People.Features;

using System.ComponentModel.DataAnnotations;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources;
using Resources.Entrypoints.Http;

public sealed class GetPersonListEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/people", Handle)
        .WithName("GetPersonList")
        .WithTags("People")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets a paginated list of the directory of the caller, by name.
    /// </summary>
    public static async Task<Ok<PagedList<PersonDto>>> Handle(
        [Range(1, BasePaginationParameters.MaxPageNumber)] int? pageNumber,
        [Range(1, int.MaxValue)] int? pageSize,
        IMediator mediator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetPersonList.Query(PersonParametersDto.From(pageNumber, pageSize)), cancellationToken));
}
