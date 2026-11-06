namespace Recall.Server.Resources.Storage;

using Entrypoints.Http;
using Microsoft.AspNetCore.Http.HttpResults;

public sealed record FileStoreStatusDto(bool CanWrite);

public sealed class GetFileStoreStatusEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/file-store/status", Handle)
        .WithName("GetFileStoreStatus")
        .WithTags("FileStore")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Tells a client whether the file store takes writes, so a person sees the fault before a recording is at risk.
    /// </summary>
    public static async Task<Ok<FileStoreStatusDto>> Handle(FileStoreHealthCheck check, CancellationToken cancellationToken)
        => TypedResults.Ok(new FileStoreStatusDto(await check.TakesWritesAsync(cancellationToken)));
}
