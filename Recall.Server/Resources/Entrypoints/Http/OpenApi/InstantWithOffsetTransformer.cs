namespace Recall.Server.Resources.Entrypoints.Http.OpenApi;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>Types each property with <see cref="InstantWithOffsetConverter"/> as a string. The generator knows no type for a property with its own converter.</summary>
public sealed class InstantWithOffsetTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.CustomConverter is InstantWithOffsetConverter)
            schema.Type = JsonSchemaType.String;

        return Task.CompletedTask;
    }
}
