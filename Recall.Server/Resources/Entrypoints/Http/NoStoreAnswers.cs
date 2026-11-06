namespace Recall.Server.Resources.Entrypoints.Http;

/// <summary>
/// Each answer of the Server tells the browser to store nothing, so that its HTTP cache keeps no private answer after a
/// logout. It runs after the static files, which hold no private data. The exception handler clears the headers of an
/// answer that fails, and then sets its own <c>no-cache,no-store</c>.
/// </summary>
public static class NoStoreAnswers
{
    public static IApplicationBuilder UseNoStoreAnswers(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
}
