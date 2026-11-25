namespace Recall.Server.Resources.Entrypoints.Jobs;

using Auth;
using BackWave.Dashboard;

public static class JobDashboard
{
    public const string Path = "/backwave";

    /// <summary>
    /// A live page holds one request open, and its View permission is checked only at the start of the request. The
    /// browser opens a new request after this time, so a person who is no longer the Owner stops seeing job data.
    /// </summary>
    public static readonly TimeSpan LiveStreamLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A browser that opens the dashboard sends no <c>X-CSRF</c> header. The session cookie is SameSite=Strict, and
    /// BackWave checks an antiforgery token on each write.
    /// </summary>
    public static bool Serves(PathString path) => path.StartsWithSegments(Path);

    /// <summary>
    /// BackWave writes its scripts and its styles inline in each page, so a hash would break at each update of the
    /// package. The other directives keep the page from a frame and from each other origin.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Call it before the exception handler. The last callback to register runs first, so this one runs last. Then it
    /// has the last word over the antiforgery code, which writes <c>SAMEORIGIN</c> when it issues a cookie, over the
    /// exception handler, which clears the headers, and over the <c>no-cache</c> of the live stream.
    /// </summary>
    public static void UseJobDashboardAnswerHeaders(this WebApplication app) => app.Use((context, next) =>
    {
        if (Serves(context.Request.Path))
            context.Response.OnStarting(SetAnswerHeaders, context.Response);

        return next(context);
    });

    private static Task SetAnswerHeaders(object state)
    {
        var headers = ((HttpResponse)state).Headers;
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        // A refusal page and a fault page are stricter, and they refuse frames too.
        if (headers.ContentSecurityPolicy.Count == 0)
            headers.ContentSecurityPolicy = ContentSecurityPolicy;

        return Task.CompletedTask;
    }

    /// <summary>Call it after <c>UseAuthentication</c>, because the callbacks read the caller of the request.</summary>
    public static void UseJobDashboard(this WebApplication app)
    {
        app.UseWhen(context => Serves(context.Request.Path), dashboard => dashboard.Use(LimitLiveStreamAndExplainRefusalAsync));
        app.UseBackWaveDashboard(Path, DashboardOptions);
    }

    public static readonly BackWaveDashboardOptions DashboardOptions = new()
    {
        AuthorizeView = JobOwnerCheck.PassesAsync,
        AuthorizeRequeue = JobOwnerCheck.PassesAsync,
        AuthorizeCancel = JobOwnerCheck.PassesAsync,
        AuthorizePauseQueue = JobOwnerCheck.PassesAsync,
        AuthorizeTriggerSchedule = JobOwnerCheck.PassesAsync,
        ExposeSensitiveData = false,
        ResolveActor = JobOwnerCheck.ActorOf,
    };

    public static async Task LimitLiveStreamAndExplainRefusalAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Query["live"] == "1")
        {
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            var requestAborted = context.RequestAborted;
            using var end = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopping);
            end.CancelAfter(LiveStreamLimit);
            context.RequestAborted = end.Token;
            await next(context);
            context.RequestAborted = requestAborted;
            return;
        }

        await next(context);
        var isGet = HttpMethods.IsGet(context.Request.Method);
        if (context.Response.StatusCode == StatusCodes.Status403Forbidden && (isGet || HttpMethods.IsPost(context.Request.Method)) && !context.Response.HasStarted)
        {
            // SameSite=Strict holds the cookie back on the redirect from the site of a provider. A reload comes from this site, so it sends the cookie.
            if (isGet && context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                context.Response.Headers["Refresh"] = "1";

            await AuthPages.OwnerOnly(Path).ExecuteAsync(context);
        }
    }
}
