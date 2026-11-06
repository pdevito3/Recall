namespace Recall.UnitTests.Domain.Recordings;

using Recall.Transcriber;
using Shouldly;

public class ChunkCutterTests
{
    private const int Max = 16_000;

    private static float[] Speech(int length) => Enumerable.Repeat(0.5f, length).ToArray();

    [Test]
    public void a_window_that_fits_ends_at_its_end()
    {
        // Arrange
        var window = Speech(Max);

        // Act
        var cut = ChunkCutter.CutLength(window, Max);

        // Assert
        cut.ShouldBe(Max);
    }

    [Test]
    public void a_window_with_no_quiet_run_ends_at_the_limit()
    {
        // Arrange
        var window = Speech(Max + 1);

        // Act
        var cut = ChunkCutter.CutLength(window, Max);

        // Assert
        cut.ShouldBe(Max);
    }

    [Test]
    public void a_cut_falls_in_the_middle_of_the_longest_quiet_run()
    {
        // Arrange
        var window = Speech(Max + 1);
        Array.Clear(window, 12_800, 1_600);
        Array.Clear(window, 9_600, 320);

        // Act
        var cut = ChunkCutter.CutLength(window, Max);

        // Assert
        cut.ShouldBe(12_800 + 800);
    }

    [Test]
    public void a_quiet_run_before_the_last_half_is_not_a_cut()
    {
        // Arrange
        var window = Speech(Max + 1);
        Array.Clear(window, 0, 3_200);

        // Act
        var cut = ChunkCutter.CutLength(window, Max);

        // Assert
        cut.ShouldBe(Max);
    }
}
