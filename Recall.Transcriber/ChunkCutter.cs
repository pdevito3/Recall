namespace Recall.Transcriber;

public static class ChunkCutter
{
    public const int FrameSamples = 320;

    private const float QuietRms = 0.003f;

    public static int CutLength(ReadOnlySpan<float> window, int maxSamples)
    {
        if (window.Length <= maxSamples)
            return window.Length;

        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;
        for (var frame = maxSamples / 2; frame + FrameSamples <= maxSamples; frame += FrameSamples)
        {
            if (IsQuiet(window.Slice(frame, FrameSamples)))
            {
                if (runStart < 0)
                    runStart = frame;

                var length = frame + FrameSamples - runStart;
                if (length > bestLength)
                {
                    bestStart = runStart;
                    bestLength = length;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        return bestStart < 0 ? maxSamples : bestStart + bestLength / 2;
    }

    private static bool IsQuiet(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var sample in frame)
            sum += sample * sample;

        return Math.Sqrt(sum / frame.Length) < QuietRms;
    }
}
