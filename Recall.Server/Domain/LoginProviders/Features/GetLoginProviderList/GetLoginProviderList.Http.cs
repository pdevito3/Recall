namespace Recall.Server.Domain.LoginProviders.Features;

using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Auth;
using Resources.Entrypoints.Http;

public sealed class GetLoginProviderListEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/login-providers", Handle)
        .WithName("GetLoginProviders")
        .WithTags("LoginProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Lists the login providers of the install: the ones from the app, and the ones from <c>Auth:Providers</c>, which the app cannot
    /// change. It gives the callback URL to register at a provider. Only a person signed in to Recall can read it.
    /// </summary>
    public static async Task<Ok<LoginProviderListDto>> Handle(HttpContext http, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(
            new GetLoginProviderList.Query(AuthRoutes.OriginOf(http.Request) + AuthRoutes.Callback), cancellationToken));
}
