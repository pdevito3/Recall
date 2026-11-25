namespace Recall.Server.Resources.Entrypoints.Jobs;

using BackWave.Hosting;
using BackWave.Pro.Mcp;
using Domain.Users;

/// <summary>
/// The BackWave MCP server, for the Owner only. It takes a bearer credential only: <see cref="Auth.AppSessionHandler"/>
/// refuses a session cookie on this path, also with <c>X-CSRF: 1</c>.
/// </summary>
public static class JobMcp
{
    public const string Path = "/backwave-mcp";

    private static readonly object OwnerIdItem = new();

    public static bool Serves(PathString path) => path.StartsWithSegments(Path);

    /// <summary>
    /// Call it inside <c>AddBackWave</c>. BackWave allows a view by default, so each callback that BackWave allows by
    /// default is set here. Of the write actions, the Owner gets only a requeue. The payload and the output of a job
    /// stay hidden.
    /// </summary>
    public static void Add(BackWaveBuilder backwave) => backwave.AddMcp(options =>
    {
        options.AuthorizeView = CallerIsOwnerAsync;
        options.AuthorizeRequeue = CallerIsOwnerAsync;
        options.ExposeSensitiveData = false;
        // A write runs after its permission callback in the same request, so the callback has kept the user ID.
        options.ResolveActor = context => context.Items[OwnerIdItem] as string ?? "unknown";
    });

    /// <summary>A request with no credential gets a 401 with a Bearer challenge from the authorization of the route.</summary>
    public static void MapJobMcp(this WebApplication app) => app.MapBackWaveProMcp(Path).RequireAuthorization();

    private static async ValueTask<bool> CallerIsOwnerAsync(HttpContext context)
    {
        var ownerGuard = context.RequestServices.GetRequiredService<IOwnerGuard>();
        var ownerId = await ownerGuard.CallerIfOwnerAsync(context.RequestAborted);
        context.Items[OwnerIdItem] = ownerId;
        return ownerId is not null;
    }
}
