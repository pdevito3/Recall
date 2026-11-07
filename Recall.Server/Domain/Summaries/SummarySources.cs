namespace Recall.Server.Domain.Summaries;

using System.Security.Cryptography;
using System.Text;

/// <summary>What a Summary needs to know of one Session with words: its Journaling day and the numbers of its two Drafts.</summary>
public sealed record SessionStub(string Id, DateOnly JournalingDay, int RawRevision, int CleanedRevision);

/// <summary>
/// The sources of a period as they stand now. <see cref="Fingerprint"/> changes when a source Session changes its words, moves
/// in or out of the period, or goes away, and it changes up the chain, because a roll-up hashes the fingerprints of its sources.
/// </summary>
public readonly record struct SourceState(int Count, string Fingerprint)
{
    public static readonly SourceState None = new(0, string.Empty);

    public bool HasSources => Count > 0;
}

/// <summary>
/// The roll-up of one User, from the Sessions with words. Day and Week read Sessions. Month reads the Days that have Sessions,
/// Quarter the Months, and Year the Quarters. It reads no clock and no database.
/// </summary>
public sealed class SummarySources(IEnumerable<SessionStub> sessions)
{
    private readonly ILookup<DateOnly, SessionStub> _byDay = sessions.ToLookup(s => s.JournalingDay);
    private readonly Dictionary<PeriodKey, SourceState> _states = [];

    public SourceState StateOf(PeriodKey key)
    {
        if (_states.TryGetValue(key, out var known))
            return known;

        var state = key.SourcePeriod is null ? FromSessions(SessionsIn(key)) : FromSources(key.SourcePeriods().Select(source => (source, StateOf(source))));
        _states[key] = state;
        return state;
    }

    /// <summary>The Sessions of a period that reads Sessions, in order of their day and then their ID.</summary>
    public IReadOnlyList<SessionStub> SessionsIn(PeriodKey key)
    {
        var sessions = new List<SessionStub>();
        for (var day = key.Start; day < key.End; day = day.AddDays(1))
            sessions.AddRange(_byDay[day].OrderBy(s => s.Id, StringComparer.Ordinal));
        return sessions;
    }

    /// <summary>The source periods of a roll-up that have sources, in date order.</summary>
    public IReadOnlyList<PeriodKey> SourcePeriodsWithSources(PeriodKey key) => key.SourcePeriods().Where(source => StateOf(source).HasSources).ToList();

    private static SourceState FromSessions(IReadOnlyList<SessionStub> sessions)
        => sessions.Count == 0
            ? SourceState.None
            : new SourceState(sessions.Count, Hash(string.Join('\n', sessions.Select(s => $"{s.JournalingDay:yyyy-MM-dd}:{s.Id}:{s.RawRevision}:{s.CleanedRevision}"))));

    private static SourceState FromSources(IEnumerable<(PeriodKey Key, SourceState State)> sources)
    {
        var withSources = sources.Where(source => source.State.HasSources).ToList();
        return withSources.Count == 0
            ? SourceState.None
            : new SourceState(withSources.Count, Hash(string.Join('\n', withSources.Select(source => $"{source.Key.Start:yyyy-MM-dd}:{source.State.Fingerprint}"))));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
