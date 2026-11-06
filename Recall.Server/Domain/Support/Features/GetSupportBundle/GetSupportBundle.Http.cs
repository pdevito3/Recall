namespace Recall.Server.Domain.Support.Features;

using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Resources.Entrypoints.Http;

public sealed class GetSupportBundleEndpoint : IEndpoint
{
    private const string ZipContentType = "application/zip";

    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/support-bundle", Handle)
        .WithName("GetSupportBundle")
        .WithTags("Support")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .Produces<Stream>(StatusCodes.Status200OK, ZipContentType)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets one zip with the log and telemetry files, the version, and the settings with the secrets hidden. Only the Owner
    /// of the install can call this.
    /// </summary>
    public static async Task<PushStreamHttpResult> Handle(IMediator mediator, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var bundle = await mediator.Send(new GetSupportBundle.Query(), cancellationToken);

        // A zip entry writes its last bytes, about 24, in a dispose that has no async form
        httpContext.Features.GetRequiredFeature<IHttpBodyControlFeature>().AllowSynchronousIO = true;

        return TypedResults.Stream(output => bundle.WriteToAsync(output, cancellationToken), ZipContentType, bundle.FileName);
    }
}
