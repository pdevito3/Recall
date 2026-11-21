namespace Recall.Server.Auth;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The wait of PWD-12, for one pair of a typed login name and a connection address. The pair, not the account, waits,
/// so a stranger cannot lock the Owner out. A name that no credential holds counts the same, so the wait tells nothing.
/// The pairs live in memory, and a restart clears them. A pair is a SHA-256 of the folded name and the address bucket, so
/// a long typed name takes no more memory than a short one, and at most <see cref="MaxPairs"/> pairs stay.
/// </summary>
public sealed class PasswordBackoff(TimeProvider timeProvider)
{
    public const int FreeTries = 5;

    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

    /// <summary>The most pairs in memory. Above it, the pairs with the oldest wrong try go, down to 9 in 10 of it.</summary>
    public const int MaxPairs = 10_000;

    private readonly ConcurrentDictionary<string, Pair> _pairs = new();
    private readonly Lock _evictGate = new();

    internal int Count => _pairs.Count;

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
        if (_pairs.Count > MaxPairs)
            EvictOldest();
    }

    public void NoteRight(string? typedName, IPAddress? address) => _pairs.TryRemove(KeyOf(typedName, address), out _);

    // Names that a stranger makes up cannot fill the memory. One scan frees a tenth of the cap, so the next adds do not scan.
    private void EvictOldest()
    {
        lock (_evictGate)
        {
            if (_pairs.Count <= MaxPairs)
                return;

            var over = _pairs.Count - MaxPairs * 9 / 10;
            foreach (var (key, _) in _pairs.OrderBy(pair => pair.Value.LastWrong).Take(over).ToList())
                _pairs.TryRemove(key, out _);
        }
    }

    // The length of the name comes first, so no name and bucket give the text of another pair.
    private static string KeyOf(string? typedName, IPAddress? address)
    {
        var name = LoginName.KeyOf(typedName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{name.Length}:{name}{ConnectionAddress.BucketOf(address)}")));
    }

    private readonly record struct Pair(int WrongTries, DateTimeOffset LastWrong);
}
