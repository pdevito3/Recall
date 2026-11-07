namespace Recall.Server.Domain.Sessions;

public sealed record DraftSlot(string Text, int DraftRevision, int LatestRevision, DateTimeOffset? SavedAt)
{
    public const int NoRevision = 0;

    public static readonly DraftSlot Empty = new(string.Empty, NoRevision, NoRevision, null);

    public DraftDecision Decide(DraftSave save, DraftAnswer? earlier = null)
    {
        if (earlier is { DraftTookAt: var number })
            return new DraftDecision(this, number is null ? DraftSaveOutcome.Lost : DraftSaveOutcome.Kept, null, number);

        if (save.Text == Text)
            return new DraftDecision(this, DraftSaveOutcome.Unchanged, null, DraftRevision);

        var next = LatestRevision + 1;
        return save.BaseRevision == DraftRevision
            ? new DraftDecision(new DraftSlot(save.Text, next, next, save.SavedAt), DraftSaveOutcome.Won, next, next)
            : new DraftDecision(this with { LatestRevision = next }, DraftSaveOutcome.Lost, next, null);
    }

    public DraftSlot Rewritten(string text, DateTimeOffset now) => new(text, LatestRevision + 1, LatestRevision + 1, now);
}

/// <summary>The answer of the first try of a save. <paramref name="DraftTookAt"/> is the number at which the Draft took the words, and null when this save lost. SES-8.</summary>
public readonly record struct DraftAnswer(int? DraftTookAt);

/// <summary>What a save does to a Draft. <c>DraftRevision</c> is the number that the reply names, or null after a loss. SES-36.</summary>
public readonly record struct DraftDecision(DraftSlot Next, DraftSaveOutcome Outcome, int? MintedRevision, int? DraftRevision);
