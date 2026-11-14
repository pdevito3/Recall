namespace Recall.UnitTests.Domain.Recordings;

using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

public class ParagraphBreaksTests
{
    private const int SamplesPerMs = Recording.SamplesPerSecond / 1000;

    public sealed record Chunk(int[] TapsMs, long FromMs, long EndMs, long TotalMs, string Words, (int StartMs, int EndMs)[]? Spans,
        bool StartsParagraph = false, bool DraftIsFromChunk = true);

    public sealed record Expected(string[]? Paragraphs, bool Joins, bool NextWordsStartParagraph);

    private static ParagraphBreaks.Cut CutOf(Chunk chunk)
        => ParagraphBreaks.Of(chunk.TapsMs, chunk.StartsParagraph, chunk.DraftIsFromChunk,
            chunk.FromMs * SamplesPerMs, chunk.EndMs * SamplesPerMs, chunk.TotalMs * SamplesPerMs, chunk.Words,
            chunk.Spans?.Select(s => new WordTime(s.StartMs * SamplesPerMs, s.EndMs * SamplesPerMs)).ToList());

    private static void ShouldGive(ParagraphBreaks.Cut cut, Chunk chunk, Expected expected)
    {
        cut.NextWordsStartParagraph.ShouldBe(expected.NextWordsStartParagraph);
        if (expected.Paragraphs is null)
        {
            cut.Added.ShouldBeNull();
            return;
        }

        cut.Added.ShouldNotBeNull();
        cut.Added.Paragraphs.ShouldBe(expected.Paragraphs);
        cut.Added.JoinsLastParagraph.ShouldBe(expected.Joins);
        string.Join(" ", cut.Added.Paragraphs).ShouldBe(chunk.Words);
    }

    private static readonly (int, int)[] NarrowAndWide = [(100, 500), (600, 1000), (1600, 2000), (2050, 2500)];

    private static readonly (int, int)[] FourSecondPause = [(100, 500), (600, 1_000), (5_000, 5_400), (5_500, 5_900)];

    public static IEnumerable<Func<(string Case, Chunk Chunk, Expected Expected)>> MeasuredCases()
    {
        yield return () => ("the widest gap in the window wins over a nearer narrow one",
            new([2_000], 0, 2_600, 2_600, "one two. three four", NarrowAndWide),
            new(["one two.", "three four"], true, false));
        yield return () => ("two taps in one gap give one break",
            new([1_200, 1_400], 0, 2_600, 2_600, "one two. three four", NarrowAndWide),
            new(["one two.", "three four"], true, false));
        yield return () => ("a tie goes to the earliest gap",
            new([1_000], 0, 2_100, 2_100, "a b c", [(100, 500), (800, 1_200), (1_500, 2_000)]),
            new(["a", "b c"], true, false));
        yield return () => ("a tap with no gap in its window takes the nearest, not the widest",
            new([6_500], 0, 10_000, 10_000, "alpha beta", [(2_000, 4_000), (4_100, 9_900)]),
            new(["alpha", "beta"], true, false));
        yield return () => ("a tap within a second before a cut takes the cut, whose width is doubled",
            new([9_300], 0, 10_000, 20_000, "a b c.", [(100, 400), (8_000, 8_600), (8_900, 9_700)]),
            new(["a b c."], true, true));
        yield return () => ("a tap within a second after a cut takes the cut, whose width is doubled",
            new([10_500], 10_000, 20_000, 20_000, "d e f", [(10_200, 10_800), (11_050, 11_500), (11_600, 19_000)]),
            new(["d e f"], false, false));
        yield return () => ("a tap 0.3 s into a pause of 4 s takes the pause, not a narrow gap whose middle is nearer",
            new([1_300], 0, 6_000, 6_000, "one two. three four", FourSecondPause),
            new(["one two.", "three four"], true, false));
        yield return () => ("a tap 0.3 s before the end of a pause of 4 s takes the pause, not a narrow gap whose middle is nearer",
            new([4_700], 0, 6_000, 6_000, "one two. three four", FourSecondPause),
            new(["one two.", "three four"], true, false));
        yield return () => ("a wide gap whose edge is near the tap but whose middle is far does not win",
            new([1_300], 0, 5_400, 5_400, "one two. three four five", [(100, 500), (600, 1_000), (1_400, 1_800), (1_850, 2_000), (5_000, 5_400)]),
            new(["one two.", "three four five"], true, false));
        yield return () => ("a tap 0.3 s into a pause of 4 s across a cut takes the cut",
            new([8_300], 0, 10_000, 20_000, "alpha beta gamma.", [(5_000, 6_500), (6_600, 7_400), (7_500, 8_000)]),
            new(["alpha beta gamma."], true, true));
        yield return () => ("a tap 0.3 s before the end of a pause of 4 s across a cut takes the cut",
            new([11_700], 10_000, 20_000, 20_000, "delta epsilon zeta", [(12_000, 12_500), (12_600, 13_000), (13_100, 19_000)]),
            new(["delta epsilon zeta"], false, false));
        yield return () => ("a tap before the first word breaks before the words",
            new([10_500], 10_000, 20_000, 20_000, "x y", [(12_000, 12_500), (12_600, 13_000)]),
            new(["x y"], false, false));
        yield return () => ("a tap after the last word breaks before the next words",
            new([9_000], 0, 10_000, 10_000, "x y", [(100, 500), (600, 1_000)]),
            new(["x y"], true, true));
        yield return () => ("a tap in a chunk with no words breaks before the next words",
            new([5_000], 0, 10_000, 20_000, "", null),
            new(null, false, true));
        yield return () => ("a chunk with no words and no tap keeps a break that waits",
            new([15_000], 0, 10_000, 20_000, "", null, StartsParagraph: true),
            new(null, false, true));
        yield return () => ("a chunk with no words and no tap adds no break",
            new([15_000], 0, 10_000, 20_000, "", null),
            new(null, false, false));
        yield return () => ("a tap past the end of the audio gives no break",
            new([12_000], 0, 10_000, 10_000, "x y", [(100, 500), (600, 1_000)]),
            new(["x y"], true, false));
        yield return () => ("a tap of another chunk gives no break here",
            new([15_000], 0, 10_000, 20_000, "x y", [(100, 500), (600, 1_000)]),
            new(["x y"], true, false));
        yield return () => ("a break that waits starts the first words",
            new([15_000], 0, 10_000, 20_000, "x y", [(100, 500), (600, 1_000)], StartsParagraph: true),
            new(["x y"], false, false));
    }

    [Test]
    [Property("Rule", "REC-26")]
    [MethodDataSource(nameof(MeasuredCases))]
    public void with_measured_times_a_tap_breaks_at_the_widest_gap_near_it_and_never_inside_a_word(string @case, Chunk chunk, Expected expected)
    {
        // Act
        var cut = CutOf(chunk);

        // Assert
        ShouldGive(cut, chunk, expected);
    }

    private const string ThirtyCharacters = "aaaa bbbb cccc. dddd eeee ffff";
    private static readonly string[] SplitAtTheSentenceEnd = ["aaaa bbbb cccc.", "dddd eeee ffff"];

    private static readonly string FortyWords = string.Join(" ", Enumerable.Range(1, 40).Select(i => i == 5 ? "w05." : $"w{i:00}"));

    public static IEnumerable<Func<(string Case, Chunk Chunk, Expected Expected)>> EstimatedCases()
    {
        yield return () => ("no times: the break snaps to a sentence end within 12 words",
            new([2_100], 0, 3_000, 6_000, ThirtyCharacters, null),
            new(SplitAtTheSentenceEnd, true, false));
        yield return () => ("a closing bracket may follow the sentence end",
            new([2_000], 0, 2_900, 6_000, "aaaa bbbb cc!) dddd eeee ffff", null),
            new(["aaaa bbbb cc!)", "dddd eeee ffff"], true, false));
        yield return () => ("with no sentence end within 12 words the estimated boundary stays",
            new([8_050], 0, 16_000, 32_000, FortyWords, null),
            new([string.Join(" ", FortyWords.Split(' ')[..20]), string.Join(" ", FortyWords.Split(' ')[20..])], true, false));
        yield return () => ("times out of order are not used",
            new([2_100], 0, 3_000, 6_000, ThirtyCharacters, [(0, 100), (2_600, 2_700), (1_900, 2_000), (2_050, 2_150), (2_200, 2_300), (2_400, 2_500)]),
            new(SplitAtTheSentenceEnd, true, false));
        yield return () => ("a time that ends before it starts is not used",
            new([2_100], 0, 3_000, 6_000, ThirtyCharacters, [(0, 100), (1_500, 1_400), (2_600, 2_700), (2_750, 2_800), (2_850, 2_900), (2_950, 3_000)]),
            new(SplitAtTheSentenceEnd, true, false));
        yield return () => ("times of another count of words are not used",
            new([2_100], 0, 3_000, 6_000, ThirtyCharacters, [(0, 100), (200, 300), (2_000, 2_050), (2_060, 2_070), (2_080, 2_090)]),
            new(SplitAtTheSentenceEnd, true, false));
    }

    [Test]
    [Property("Rule", "REC-29")]
    [MethodDataSource(nameof(EstimatedCases))]
    public void with_estimated_times_a_tap_breaks_at_a_sentence_end_near_its_share_of_the_chunk(string @case, Chunk chunk, Expected expected)
    {
        // Act
        var cut = CutOf(chunk);

        // Assert
        ShouldGive(cut, chunk, expected);
    }

    [Test]
    [Property("Rule", "REC-27")]
    public void a_recording_with_no_taps_gives_each_chunk_its_own_paragraph_as_before()
    {
        // Arrange
        var chunk = new Chunk([], 16_000, 32_000, 48_000, "next words", [(16_100, 16_500), (16_600, 17_000)]);

        // Act
        var cut = CutOf(chunk);
        var silent = CutOf(chunk with { Words = "", Spans = null });

        // Assert
        ShouldGive(cut, chunk, new(["next words"], false, false));
        silent.ShouldBe(new ParagraphBreaks.Cut(null, false));
    }

    [Test]
    [Property("Rule", "REC-27")]
    public void a_chunk_after_an_edit_of_raw_starts_a_paragraph_so_it_never_joins_the_words_of_the_person()
    {
        // Arrange
        var chunk = new Chunk([2_000], 0, 2_600, 2_600, "one two. three four", NarrowAndWide, DraftIsFromChunk: false);

        // Act
        var cut = CutOf(chunk);

        // Assert
        ShouldGive(cut, chunk, new(["one two.", "three four"], false, false));
    }
}
