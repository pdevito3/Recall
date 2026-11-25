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

    public static async ValueTask<bool> PassesAsync(HttpContext context)
    {
        var ownerGuard = context.RequestServices.GetRequiredService<IOwnerGuard>();
        var ownerId = await ownerGuard.CallerIfOwnerAsync(context.RequestAborted);
        context.Items[OwnerIdItem] = ownerId;
        return ownerId is not null;
    }

    public static string ActorOf(HttpContext context) => context.Items[OwnerIdItem] as string ?? "unknown";
}
