namespace Recall.Server.Resources.Entrypoints.Jobs;

using Domain.Users;

/// <summary>
/// BackWave runs a write after its permission callback in the same request, so the check keeps the user ID that it
/// found, and <see cref="ActorOf"/> gives it to the audit record of BackWave.
/// </summary>
internal static class JobOwnerCheck
{
    private static readonly object OwnerIdItem = new();

    public static ValueTask<bool> PassesForDashboardAsync(HttpContext context)
        => KeepAsync(context, (guard, cancellationToken) => guard.CallerIfOwnerAsync(cancellationToken));

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
