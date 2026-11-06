namespace Recall.Server.Domain.Sessions;

/// <summary>
/// Last-write-wins for a row with no Revisions, on the time that the client claims. A claim after now gets the time of the
/// server, so its retry gets a later time and looks newer than the changes between them. A row remembers such a claim
/// until its save time passes the claim. From then on the claim is not newer by its own time, so it needs no entry.
/// One mark in place of the list cannot hold: under a mark, a late retry and a new edit from a right clock look the same.
/// A kept claim stops only a change with its exact time, so a clock years ahead cannot stop a change with another time.
/// </summary>
public static class FastClockClaims
{
    public static DateTimeOffset SaveTimeOf(DateTimeOffset claimed, DateTimeOffset now)
    {
        var time = claimed < now ? claimed : now;
        return new DateTimeOffset(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    public static bool IsNewer(this DateTimeOffset claimed, DateTimeOffset? savedAt, IReadOnlyList<long> claims, DateTimeOffset now)
        => !claims.Contains(claimed.UtcTicks) && (savedAt is null || SaveTimeOf(claimed, now) > savedAt);

    public static IReadOnlyList<long> Remembering(this IReadOnlyList<long> claims, DateTimeOffset claimed, DateTimeOffset savedAt)
        => [.. claims.Append(claimed.UtcTicks).Where(claim => claim > savedAt.UtcTicks)];
}
