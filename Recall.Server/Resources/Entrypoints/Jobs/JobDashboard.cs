namespace Recall.Server.Resources.Entrypoints.Jobs;

using Auth;
using BackWave.Dashboard;
using Domain.Users;

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

    /// <summary>Call it after <c>UseAuthentication</c>, because the callbacks read the caller of the request.</summary>
    public static void UseJobDashboard(this WebApplication app)
    {
        app.UseWhen(context => Serves(context.Request.Path), dashboard => dashboard.Use(LimitLiveStreamAndExplainRefusalAsync));
        app.UseBackWaveDashboard(Path, DashboardOptions);
    }

    public static readonly BackWaveDashboardOptions DashboardOptions = new()
    {
        AuthorizeView = CallerIsOwnerAsync,
        AuthorizeRequeue = CallerIsOwnerAsync,
        AuthorizeCancel = CallerIsOwnerAsync,
        AuthorizePauseQueue = CallerIsOwnerAsync,
        AuthorizeTriggerSchedule = CallerIsOwnerAsync,
        ExposeSensitiveData = false,
        ResolveActor = context => context.User.FindFirst("sub")?.Value ?? "unknown",
    };

    private static async Task LimitLiveStreamAndExplainRefusalAsync(HttpContext context, RequestDelegate next)
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
        if (context.Response.StatusCode == StatusCodes.Status403Forbidden && HttpMethods.IsGet(context.Request.Method) && !context.Response.HasStarted)
        {
            // SameSite=Strict holds the cookie back on the redirect from the site of a provider. A reload comes from this site, so it sends the cookie.
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                context.Response.Headers["Refresh"] = "1";

            await AuthPages.OwnerOnly(Path).ExecuteAsync(context);
        }
    }

    private static async ValueTask<bool> CallerIsOwnerAsync(HttpContext context)
    {
        var ownerGuard = context.RequestServices.GetRequiredService<IOwnerGuard>();
        return await ownerGuard.CallerIfOwnerAsync(context.RequestAborted) is not null;
    }
}
