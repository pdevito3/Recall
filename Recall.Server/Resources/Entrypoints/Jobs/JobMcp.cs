namespace Recall.Server.Resources.Entrypoints.Jobs;

using BackWave.Hosting;
using BackWave.Pro.Mcp;

/// <summary>
/// The BackWave MCP server, for the Owner only: a native session of the Owner, or a personal access token of the Owner
/// whose grant holds the job tools. It takes a bearer credential only: <see cref="Auth.AppSessionHandler"/>
/// refuses a session cookie on this path, also with <c>X-CSRF: 1</c>.
/// </summary>
public static class JobMcp
{
    public const string Path = "/backwave-mcp";

    public static bool Serves(PathString path) => path.StartsWithSegments(Path);

    /// <summary>BackWave allows a view by default, so each callback that BackWave allows by default is set here.</summary>
    public static void Add(BackWaveBuilder backwave) => backwave.AddMcp(options =>
    {
        options.AuthorizeView = JobOwnerCheck.PassesForJobToolsAsync;
        options.AuthorizeRequeue = JobOwnerCheck.PassesForJobToolsAsync;
        options.ExposeSensitiveData = false;
        options.ResolveActor = JobOwnerCheck.ActorOf;
    });

    public static void MapJobMcp(this WebApplication app) => app.MapBackWaveProMcp(Path).RequireAuthorization();
}
