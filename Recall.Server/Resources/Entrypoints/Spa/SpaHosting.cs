namespace Recall.Server.Resources.Entrypoints.Spa;

public static class SpaHosting
{
    private const string IndexFile = "index.html";

    private static readonly PathString[] ReservedPrefixes =
        ["/api", "/auth", "/mcp", "/swagger", "/openapi", "/health", "/alive", "/.well-known"];

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

    public static void UseApiNotFound(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.GetEndpoint() is null && context.Request.Path.StartsWithSegments("/api"))
        {
            await Results.Problem(statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
            return;
        }

        await next(context);
    });

    public static bool ServesIndex(HttpContext context)
    {
        var request = context.Request;
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && context.GetEndpoint() is null
            && (!Path.HasExtension(request.Path.Value) || AsksForAPage(request))
            && !ReservedPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix));
    }

    // A path with a dot can be a missing file, and a script tag must then get a 404 and no HTML. A person who opens the path asks for a page.
    private static bool AsksForAPage(HttpRequest request)
        => request.GetTypedHeaders().Accept.Any(accept => accept.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
}
