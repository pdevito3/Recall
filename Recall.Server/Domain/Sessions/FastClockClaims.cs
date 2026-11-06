namespace Recall.Server.Domain.Sessions;

/// <summary>
/// Last-write-wins for a row with no Revisions, on the time that the client claims. A claim after now gets the time of the
/// server, so its retry gets a later time and looks newer than the changes between them. A row remembers such a claim
/// until its save time passes the claim. From then on the claim is not newer by its own time, so it needs no entry.
/// One mark in place of the list cannot hold: under a mark, a late retry and a new edit from a right clock look the same.
/// </summary>
public static class FastClockClaims
{
    // A clock a little ahead is common. It must still save behind a claim from a far future, and a kept claim still stops its retry.
    private static readonly TimeSpan SkewAllowance = TimeSpan.FromSeconds(5);

    public static DateTimeOffset SaveTimeOf(DateTimeOffset claimed, DateTimeOffset now)
    {
        var time = claimed < now ? claimed : now;
        return new DateTimeOffset(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    public static bool IsNewer(this DateTimeOffset claimed, DateTimeOffset? savedAt, IReadOnlyList<long> claims, DateTimeOffset now)
    {
        var fastAndOlder = claimed > now + SkewAllowance && claims.Any(claim => claim > claimed.UtcTicks);
        return !fastAndOlder && !claims.Contains(claimed.UtcTicks) && (savedAt is null || SaveTimeOf(claimed, now) > savedAt);
    }

    public static IReadOnlyList<long> Remembering(this IReadOnlyList<long> claims, DateTimeOffset claimed, DateTimeOffset savedAt)
        => [.. claims.Append(claimed.UtcTicks).Where(claim => claim > savedAt.UtcTicks)];
}
