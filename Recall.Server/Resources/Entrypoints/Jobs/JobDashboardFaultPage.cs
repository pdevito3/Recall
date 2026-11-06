namespace Recall.Server.Resources.Entrypoints.Jobs;

using Auth;
using Microsoft.AspNetCore.Diagnostics;

/// <summary>A person opens the dashboard in a browser, so a fault in it is a page and not a problem document.</summary>
public sealed class JobDashboardFaultPage : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!JobDashboard.Serves(httpContext.Request.Path) || httpContext.Response.StatusCode < StatusCodes.Status500InternalServerError)
            return false;

        await AuthPages.JobDashboardFault(JobDashboard.Path).ExecuteAsync(httpContext);
        return true;
    }
}
