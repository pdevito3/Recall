namespace Recall.Server.Resources.Entrypoints.Http.OpenApi;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>
/// Documents the 507 response on each write operation. A write can reach a full disk on any route, and
/// <see cref="DomainProblemDetails"/> gives the answer at one place, so no endpoint declares it.
/// </summary>
public sealed class DiskFullResponseTransformer : IOpenApiOperationTransformer
{
    private const string StatusCode = "507";
    private const string ProblemContentType = "application/problem+json";

    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Document is null || context.Description.HttpMethod is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            return;

        var schema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken);
        context.Document.AddComponent<IOpenApiSchema>(nameof(ProblemDetails), schema);
        operation.Responses ??= [];
        operation.Responses.TryAdd(StatusCode, new OpenApiResponse
        {
            Description = "The disk of the server is full, so nothing changed.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ProblemContentType] = new() { Schema = new OpenApiSchemaReference(nameof(ProblemDetails), context.Document) },
            },
        });
    }
}
