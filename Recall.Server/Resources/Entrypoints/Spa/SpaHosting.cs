namespace Recall.Server.Resources.Entrypoints.Spa;

using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

public static class SpaHosting
{
    private const string IndexFile = "index.html";
    private const string HashedFilesFolder = "/assets";
    private const string HashedFileCacheControl = "public, max-age=31536000, immutable";

    private static readonly string[] TextAssetTypes =
        ["text/html", "text/css", "text/javascript", "application/javascript", "application/json", "image/svg+xml", "application/manifest+json"];

    private static readonly PathString[] ReservedPrefixes =
        ["/api", "/auth", "/mcp", "/swagger", "/openapi", "/health", "/alive", "/.well-known"];

    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static bool IsPresent(WebApplication app) => app.Environment.WebRootFileProvider.GetFileInfo(IndexFile).Exists;

    public static void AddSpaCompression(this IServiceCollection services)
    {
        // The default level is the fastest one, and at that level Brotli sends more bytes than gzip.
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        services.AddResponseCompression(options =>
        {
            // Only the files of the app use this middleware, and they hold no secret. An API answer is not compressed, because of BREACH.
            options.EnableForHttps = true;
            options.MimeTypes = TextAssetTypes;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
    }

    public static void UseSpaStaticFiles(this WebApplication app)
    {
        if (!IsPresent(app))
            return;

        app.UseWhen(context => !IsReserved(context.Request.Path), branch => branch.UseResponseCompression());
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = file =>
            {
                var headers = file.Context.Response.Headers;
                headers.CacheControl = file.File.Name == IndexFile ? "no-store"
                    : file.Context.Request.Path.StartsWithSegments(HashedFilesFolder) ? HashedFileCacheControl
                    : "no-cache";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            },
        });
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
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
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
            && !IsReserved(request.Path);
    }

    private static bool IsReserved(PathString path) => ReservedPrefixes.Any(prefix => path.StartsWithSegments(prefix));

    // A path with a dot can be a missing file, and a script tag must then get a 404 and no HTML. A person who opens the path asks for a page.
    private static bool AsksForAPage(HttpRequest request)
        => request.GetTypedHeaders().Accept.Any(accept => accept.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
}
