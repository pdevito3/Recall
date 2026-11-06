namespace Recall.Server.Resources.Jobs;

using BackWave.Dashboard;
using Domain.Users;

public static class JobDashboard
{
    public const string Path = "/backwave";

    /// <summary>
    /// A browser that opens the dashboard sends no <c>X-CSRF</c> header. The session cookie is SameSite=Strict, and
    /// BackWave checks an antiforgery token on each write.
    /// </summary>
    public static bool Serves(PathString path) => path.StartsWithSegments(Path);

    /// <summary>Call it after <c>UseAuthentication</c>, because the callbacks read the caller of the request.</summary>
    public static void UseJobDashboard(this WebApplication app) => app.UseBackWaveDashboard(Path, new BackWaveDashboardOptions
    {
        AuthorizeView = CallerIsOwnerAsync,
        AuthorizeRequeue = CallerIsOwnerAsync,
        AuthorizeCancel = CallerIsOwnerAsync,
        AuthorizePauseQueue = CallerIsOwnerAsync,
        AuthorizeTriggerSchedule = CallerIsOwnerAsync,
        ExposeSensitiveData = false,
        ResolveActor = context => context.User.FindFirst("sub")?.Value ?? "unknown",
    });

    private static async ValueTask<bool> CallerIsOwnerAsync(HttpContext context)
    {
        var ownerGuard = context.RequestServices.GetRequiredService<IOwnerGuard>();
        return await ownerGuard.CallerIfOwnerAsync(context.RequestAborted) is not null;
    }
}
