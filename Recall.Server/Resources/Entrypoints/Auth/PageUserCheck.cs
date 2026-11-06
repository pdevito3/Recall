namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Security.Claims;
using Recall.Server.Auth.Entities;

/// <summary>
/// All tabs of a browser share one session cookie, so a tab that a person left open sends the cookie of the next person
/// who signs in.
/// </summary>
public static class PageUserCheck
{
    public const string Header = "X-Recall-User";

    public const string MismatchType = "urn:recall:problem:page-user-mismatch";

    public const string MismatchTitle = "This page belongs to another person.";

    public const string MismatchDetail = "Another person signed in on this browser after this page opened. Sign in again.";

    public static bool Refuses(HttpRequest request, ClaimsPrincipal user)
    {
        if (!request.Path.StartsWithSegments("/api") || user.FindFirst(AppSessionHandler.ClientKindClaim)?.Value != SessionKindText.Web)
            return false;

        var named = request.Headers[Header];
        return (named.Count > 0 || !MayOmitTheName(request.Method)) && named != user.FindFirst("sub")?.Value;
    }

    public static bool MayOmitTheName(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    public static void UsePageUserCheck(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (!Refuses(context.Request, context.User))
        {
            await next(context);
            return;
        }

        await Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: MismatchType,
            title: MismatchTitle,
            detail: MismatchDetail).ExecuteAsync(context);
    });
}
