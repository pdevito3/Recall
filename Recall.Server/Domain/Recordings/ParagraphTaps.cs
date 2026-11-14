namespace Recall.Server.Domain.Recordings;

public static class ParagraphTaps
{
    /// <summary>
    /// REC-25: the taps of a finish, sorted, with no repeat, each from 0 to <see cref="Recording.MaxDuration"/>, and at most
    /// <see cref="Recording.MaxParagraphTaps"/>. A finish never refuses them: the client keeps the list it sent, so a refused
    /// list would come again on each retry and hold the audio until the sweep.
    /// </summary>
    public static IReadOnlyList<int> Clean(IReadOnlyList<int>? tapsMs)
        => tapsMs is null
            ? []
            : [.. tapsMs.Where(ms => ms >= 0 && ms <= Recording.MaxDuration.TotalMilliseconds).Distinct().Order().Take(Recording.MaxParagraphTaps)];
}
