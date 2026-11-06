namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Security.Claims;
using Recall.Server.Auth.Entities;

/// <summary>
/// All tabs of a browser share one session cookie, so a tab that a person left open sends the cookie of the next person
/// who signs in. Each browser request names the user that its page belongs to, and a page of another user gets a refusal.
/// A check in the SPA alone leaves a race, because the cookie can change while a save is on its way. A bearer token
/// belongs to one app, so a native client sends no name.
/// </summary>
public static class PageUserCheck
{
    public const string Header = "X-Recall-User";

    /// <summary>The <c>type</c> of the refusal. The SPA stops saving and leaves the page when it reads this type.</summary>
    public const string MismatchType = "urn:recall:problem:page-user-mismatch";

    public const string MismatchTitle = "This page belongs to another person.";

    public const string MismatchDetail = "Another person signed in on this browser after this page opened. Sign in again.";

    public static bool Refuses(HttpRequest request, ClaimsPrincipal user)
    {
        if (!request.Path.StartsWithSegments("/api") || user.FindFirst(AppSessionHandler.ClientKindClaim)?.Value != SessionKindText.Web)
            return false;

        var named = request.Headers[Header];
        var mayOmitTheName = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
        return (named.Count > 0 || !mayOmitTheName) && named != user.FindFirst("sub")?.Value;
    }

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
