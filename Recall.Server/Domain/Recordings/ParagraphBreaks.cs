namespace Recall.Server.Domain.Recordings;

using Sessions.Content;
using Transcription;

/// <summary>
/// Where the New paragraph taps of a Recording break the words of one chunk. A break is the index of a boundary between two
/// words (0 is before the first word, n after the last), never a character, so it cannot fall inside a word (REC-26). The
/// function is pure, and its inputs are fixed once the chunk before is saved, so a run of the same chunk again gives the
/// same answer (REC-28).
/// </summary>
public static class ParagraphBreaks
{
    /// <summary>The window of the widest gap.</summary>
    public const int SnapWindowMs = 1_000;

    /// <summary>How far, in words, an estimated break looks for a sentence end (REC-29).</summary>
    public const int SentenceEndReachWords = 12;

    private const long SamplesPerMs = Recording.SamplesPerSecond / 1000;
    private const string ClosingMarks = "\"')]}”’»";

    /// <summary>
    /// <paramref name="Added"/> is null when the chunk adds nothing to Raw. <paramref name="NextWordsStartParagraph"/> is the
    /// break that waits for the next words: a break after the last word, or a tap in a chunk with no words.
    /// </summary>
    public sealed record Cut(ChunkParagraphs? Added, bool NextWordsStartParagraph);

    private enum TimeKind
    {
        Measured,
        Estimated,
    }

    private sealed record WordTimes(IReadOnlyList<WordTime> Spans, TimeKind Kind);

    private readonly record struct Word(int Start, int End);

    private readonly record struct Boundary(long From, long To)
    {
        public long Width => To - From;

        public static Boundary Between(long previousEnd, long nextStart) => new(previousEnd, nextStart);

        public static Boundary AtStartOfAudio(long firstStart) => new(0, firstStart);

        public static Boundary AtEndOfAudio(long lastEnd, long totalSamples) => new(lastEnd, totalSamples);

        public static Boundary AtCutBefore(long firstStart, long cut) => new(cut - (firstStart - cut), firstStart);

        public static Boundary AtCutAfter(long lastEnd, long cut) => new(lastEnd, cut + (cut - lastEnd));

        public long Middle => (From + To) / 2;

        public bool Holds(long tap) => From <= tap && tap <= To;

        public long DistanceTo(long tap) => Holds(tap) ? 0 : Math.Min(Math.Abs(tap - From), Math.Abs(tap - To));
    }

    /// <param name="tapsMs">All the taps of the Recording, sorted and distinct (REC-25). Only those in [from, end) are this chunk's.</param>
    /// <param name="startsParagraph">The first words of the Recording, or a break that waits from the chunk before.</param>
    /// <param name="draftIsFromChunk">The Draft is the Revision of a chunk, so no person changed Raw since, and a join cannot land in their words.</param>
    /// <param name="fromSample">The first sample of the chunk: the pointer before it.</param>
    /// <param name="endSample">The sample after the last of the chunk.</param>
    /// <param name="totalSamples">The samples of the whole work audio, so the chunk knows when its end is the end of the audio.</param>
    /// <param name="words">The trimmed text of the chunk.</param>
    /// <param name="measured">The spans of the engine, or null. Spans that fail the check of <see cref="TimesOf"/> are not used.</param>
    public static Cut Of(
        IReadOnlyList<int> tapsMs, bool startsParagraph, bool draftIsFromChunk,
        long fromSample, long endSample, long totalSamples,
        string words, IReadOnlyList<WordTime>? measured)
    {
        if (tapsMs.Count == 0)
            return new(words.Length == 0 ? null : ChunkParagraphs.Own(words), false);

        var taps = tapsMs.Select(ms => ms * SamplesPerMs).Where(tap => fromSample <= tap && tap < endSample).ToList();
        var split = WordsOf(words);
        if (split.Count == 0)
            return new(null, startsParagraph || taps.Count > 0);

        var times = TimesOf(words, split, fromSample, endSample, measured);
        var bounds = BoundariesOf(times.Spans, fromSample, endSample, totalSamples);
        var breaks = new SortedSet<int>(taps.Select(tap => times.Kind == TimeKind.Measured
            ? WidestGapNear(bounds, tap)
            : SentenceEndNear(words, split, bounds, tap)));

        var n = split.Count;
        var inner = breaks.Where(k => k > 0 && k < n).ToList();
        var cuts = inner.Select(k => split[k].Start).Prepend(0).Append(words.Length).ToList();
        var paragraphs = cuts.Zip(cuts.Skip(1), (from, to) => words[from..to].TrimEnd()).ToList();
        var joins = draftIsFromChunk && !startsParagraph && !breaks.Contains(0);
        return new(new ChunkParagraphs(joins, paragraphs), breaks.Contains(n));
    }

    private static List<Word> WordsOf(string text)
    {
        var words = new List<Word>();
        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
                at++;
            if (at > start)
                words.Add(new Word(start, at));
        }

        return words;
    }

    private static WordTimes TimesOf(string text, List<Word> words, long fromSample, long endSample, IReadOnlyList<WordTime>? measured)
    {
        if (measured is { } spans && spans.Count == words.Count && InOrder(spans))
            return new(spans, TimeKind.Measured);

        long At(int character) => fromSample + (endSample - fromSample) * character / text.Length;
        return new([.. words.Select(word => new WordTime(At(word.Start), At(word.End)))], TimeKind.Estimated);
    }

    private static bool InOrder(IReadOnlyList<WordTime> spans)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i].Start > spans[i].End || (i > 0 && spans[i].Start < spans[i - 1].Start))
                return false;
        }

        return true;
    }

    private static Boundary[] BoundariesOf(IReadOnlyList<WordTime> spans, long fromSample, long endSample, long totalSamples)
    {
        var n = spans.Count;
        var bounds = new Boundary[n + 1];
        bounds[0] = fromSample == 0
            ? Boundary.AtStartOfAudio(spans[0].Start)
            : Boundary.AtCutBefore(spans[0].Start, fromSample);
        for (var k = 1; k < n; k++)
            bounds[k] = Boundary.Between(spans[k - 1].End, spans[k].Start);
        bounds[n] = endSample >= totalSamples
            ? Boundary.AtEndOfAudio(spans[n - 1].End, totalSamples)
            : Boundary.AtCutAfter(spans[n - 1].End, endSample);
        return bounds;
    }

    private static int WidestGapNear(Boundary[] bounds, long tap)
    {
        var window = SnapWindowMs * SamplesPerMs;
        var widest = -1;
        for (var k = 0; k < bounds.Length; k++)
        {
            if ((bounds[k].Holds(tap) || Math.Abs(bounds[k].Middle - tap) <= window) && (widest < 0 || bounds[k].Width > bounds[widest].Width))
                widest = k;
        }

        return widest >= 0 ? widest : NearestOf(bounds, tap, 0, bounds.Length - 1, _ => true);
    }

    private static int SentenceEndNear(string text, List<Word> words, Boundary[] bounds, long tap)
    {
        var n = words.Count;
        var estimate = NearestOf(bounds, tap, 0, n, _ => true);
        var sentenceEnd = NearestOf(bounds, tap,
            Math.Max(0, estimate - SentenceEndReachWords), Math.Min(n, estimate + SentenceEndReachWords),
            k => k == 0 || k == n || EndsSentence(text.AsSpan(words[k - 1].Start, words[k - 1].End - words[k - 1].Start)));
        return sentenceEnd >= 0 ? sentenceEnd : estimate;
    }

    private static int NearestOf(Boundary[] bounds, long tap, int first, int last, Func<int, bool> counts)
    {
        var nearest = -1;
        for (var k = first; k <= last; k++)
        {
            if (counts(k) && (nearest < 0 || bounds[k].DistanceTo(tap) < bounds[nearest].DistanceTo(tap)))
                nearest = k;
        }

        return nearest;
    }

    private static bool EndsSentence(ReadOnlySpan<char> word)
    {
        var end = word.TrimEnd(ClosingMarks);
        return end.Length > 0 && end[^1] is '.' or '?' or '!';
    }
}
