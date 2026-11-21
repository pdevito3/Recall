namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Server.Auth;

/// <summary>The answer of the limit of login starts. Its buckets are those of <see cref="ConnectionAddress.BucketOf"/>.</summary>
public static class LoginStartLimit
{
    public static async ValueTask RefuseAsync(OnRejectedContext context, TimeSpan window)
    {
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var left) ? left : window;
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        // A post cannot repeat from a link, so its page goes back to the login.
        var tryAgain = HttpMethods.IsGet(http.Request.Method) ? http.Request.Path + http.Request.QueryString : AuthRoutes.Login;
        await AuthPages.TooManyStarts(seconds, tryAgain).ExecuteAsync(http);
    }
}
