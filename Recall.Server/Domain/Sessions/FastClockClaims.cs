namespace Recall.Server.Domain.Sessions;

/// <summary>
/// A claim after now gets the time of the server, so its retry gets a later time and looks newer than the changes between them.
/// A row remembers such claims so that a retry does nothing. A claim that is not fast compares as itself, so it needs no entry.
/// </summary>
public static class FastClockClaims
{
    public const int MaxCount = 16;
    public const int ColumnMaxLength = MaxCount * 21;

    public static bool HoldsRetryOf(this IReadOnlyList<long> claims, DateTimeOffset claimed) => claims.Contains(claimed.UtcTicks);

    public static IReadOnlyList<long> Remembering(this IReadOnlyList<long> claims, DateTimeOffset claimed, DateTimeOffset now)
        => claimed > now ? [.. claims.TakeLast(MaxCount - 1), claimed.UtcTicks] : claims;
}
