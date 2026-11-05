namespace Recall.Server.Resources.Entrypoints.Spa;

/// <summary>
/// Serves the SPA build when the web root has <c>index.html</c>. In development Vite serves it, and this code does
/// nothing. A route of the SPA has no endpoint and no file extension, so it gets <c>index.html</c>.
/// </summary>
public static class SpaHosting
{
    private const string IndexFile = "index.html";

    private static readonly PathString[] ReservedPrefixes =
        ["/api", "/auth", "/bff", "/mcp", "/swagger", "/openapi", "/health", "/alive", "/.well-known"];

    public static bool IsPresent(WebApplication app) => app.Environment.WebRootFileProvider.GetFileInfo(IndexFile).Exists;

    public static void UseSpaStaticFiles(this WebApplication app)
    {
        if (IsPresent(app))
            app.UseStaticFiles();
    }

    public static void UseSpaFallback(this WebApplication app)
    {
        if (!IsPresent(app))
            return;

        var index = app.Environment.WebRootFileProvider.GetFileInfo(IndexFile);
        app.Use(async (context, next) =>
        {
            if (!ServesIndex(context))
            {
                await next(context);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = index.Length;
            context.Response.Headers.CacheControl = "no-cache";
            if (HttpMethods.IsGet(context.Request.Method))
                await context.Response.SendFileAsync(index, context.RequestAborted);
        });
    }

    public static bool ServesIndex(HttpContext context)
    {
        var request = context.Request;
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && context.GetEndpoint() is null
            && !Path.HasExtension(request.Path.Value)
            && !ReservedPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix));
    }
}
