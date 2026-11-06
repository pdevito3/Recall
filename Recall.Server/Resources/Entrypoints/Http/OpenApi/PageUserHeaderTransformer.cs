namespace Recall.Server.Resources.Entrypoints.Http.OpenApi;

using Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>
/// Documents the header of <see cref="PageUserCheck"/> on each operation that writes. A read checks a name that it gets, but
/// needs none, so this document names the header only where a browser must send it.
/// </summary>
public sealed class PageUserHeaderTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiresAuthentication = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        if (!requiresAuthentication || HttpMethods.IsGet(context.Description.HttpMethod ?? HttpMethods.Get))
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = PageUserCheck.Header,
            In = ParameterLocation.Header,
            Required = false,
            Description = "The `sub` of `GET /auth/me` when the page opened. A request with the session cookie must send it, and any other value gets a 409 "
                + $"problem with the type `{PageUserCheck.MismatchType}`. A request with a bearer token does not send it.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        return Task.CompletedTask;
    }
}
