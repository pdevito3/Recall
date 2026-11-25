namespace Recall.Server.Resources.Entrypoints.Jobs;

using Domain.Users;

/// <summary>
/// The Owner check of the job dashboard and of the job tools. BackWave runs a write after its permission callback in
/// the same request, so the check keeps the user ID that it found, and <see cref="ActorOf"/> gives it to the audit
/// record of BackWave.
/// </summary>
internal static class JobOwnerCheck
{
    private static readonly object OwnerIdItem = new();

    /// <summary>The check of the dashboard: the Owner, in person.</summary>
    public static ValueTask<bool> PassesAsync(HttpContext context)
        => KeepAsync(context, (guard, cancellationToken) => guard.CallerIfOwnerAsync(cancellationToken));

    /// <summary>The check of the job tools: the Owner, or a personal access token of the Owner with the job tools.</summary>
    public static ValueTask<bool> PassesForJobToolsAsync(HttpContext context)
        => KeepAsync(context, (guard, cancellationToken) => guard.JobToolsCallerIfOwnerAsync(cancellationToken));

    public static string ActorOf(HttpContext context) => context.Items[OwnerIdItem] as string ?? "unknown";

    private static async ValueTask<bool> KeepAsync(HttpContext context, Func<IOwnerGuard, CancellationToken, Task<string?>> check)
    {
        var ownerId = await check(context.RequestServices.GetRequiredService<IOwnerGuard>(), context.RequestAborted);
        context.Items[OwnerIdItem] = ownerId;
        return ownerId is not null;
    }
}
