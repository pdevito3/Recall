namespace Recall.Server.Auth;

using System.Collections.Concurrent;
using System.Net;

/// <summary>
/// The wait of PWD-12, for one pair of a typed login name and a connection address. The pair, not the account, waits,
/// so a stranger cannot lock the Owner out. A name that no credential holds counts the same, so the wait tells nothing.
/// The pairs live in memory, and a restart clears them.
/// </summary>
public sealed class PasswordBackoff(TimeProvider timeProvider)
{
    public const int FreeTries = 5;

    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

    // A pair with no wrong try for a day goes when the map is large, so names that a stranger makes up cannot fill the memory.
    private const int PruneAbove = 10_000;
    private static readonly TimeSpan PruneAfter = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<(string Name, string Address), Pair> _pairs = new();

    /// <summary>The wait after this many wrong tries in a row: none for the first <see cref="FreeTries"/>, then 30 seconds, doubling to 15 minutes.</summary>
    public static TimeSpan WaitAfter(int wrongTries)
    {
        if (wrongTries < FreeTries)
            return TimeSpan.Zero;

        var doublings = Math.Min(wrongTries - FreeTries, 5);
        var wait = FirstWait * (1 << doublings);
        return wait < LongestWait ? wait : LongestWait;
    }

    /// <summary>The time left to wait for this pair, or null when it may try now.</summary>
    public TimeSpan? WaitLeft(string? typedName, IPAddress? address)
    {
        if (!_pairs.TryGetValue(KeyOf(typedName, address), out var pair))
            return null;

        var left = pair.LastWrong + WaitAfter(pair.WrongTries) - timeProvider.GetUtcNow();
        return left > TimeSpan.Zero ? left : null;
    }

    public void NoteWrong(string? typedName, IPAddress? address)
    {
        var now = timeProvider.GetUtcNow();
        _pairs.AddOrUpdate(KeyOf(typedName, address), new Pair(1, now), (_, pair) => new Pair(pair.WrongTries + 1, now));
        if (_pairs.Count > PruneAbove)
        {
            foreach (var (key, pair) in _pairs)
            {
                if (now - pair.LastWrong > PruneAfter)
                    _pairs.TryRemove(key, out _);
            }
        }
    }

    public void NoteRight(string? typedName, IPAddress? address) => _pairs.TryRemove(KeyOf(typedName, address), out _);

    private static (string, string) KeyOf(string? typedName, IPAddress? address)
        => (LoginName.KeyOf(typedName), ConnectionAddress.BucketOf(address));

    private readonly record struct Pair(int WrongTries, DateTimeOffset LastWrong);
}
